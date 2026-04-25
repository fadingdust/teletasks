using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class TaskExecutor
{
    private readonly TaskCatalogOptions _options;
    private readonly OutputCollector _output;
    private readonly ILogger<TaskExecutor> _logger;

    public TaskExecutor(
        IOptions<TaskCatalogOptions> options,
        OutputCollector output,
        ILogger<TaskExecutor> logger)
    {
        _options = options.Value;
        _output = output;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate a task's output spec against the current state of disk WITHOUT
    /// running its command. For Images / File / LogTail outputs this just reads
    /// what's already there. For Text outputs there's no cached state, so the
    /// returned result is empty (caller should tell the user to run the task).
    ///
    /// Used by /results to read a task's latest produced output, and by
    /// /job N (long-running branch) to refresh the artifacts mid-run.
    /// </summary>
    public async Task<TaskExecutionResult> EvaluateOutputAsync(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var resolved = ApplyDefaults(task, parameters ?? new Dictionary<string, object?>());
        var result = new TaskExecutionResult { Success = true };
        await _output.CollectAsync(task, resolved, string.Empty, string.Empty, result, cancellationToken);
        return result;
    }

    private static Dictionary<string, object?> ApplyDefaults(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> supplied)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in task.Parameters)
        {
            if (supplied.TryGetValue(p.Name, out var v) && v is not null)
            {
                result[p.Name] = v;
            }
            else if (p.Default is not null)
            {
                result[p.Name] = p.Default;
            }
        }
        return result;
    }

    public async Task<TaskExecutionResult> ExecuteAsync(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveParameters(task, parameters);
        var missing = task.Parameters
            .Where(p => p.Required && !resolved.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();

        if (missing.Count > 0)
        {
            return new TaskExecutionResult
            {
                Success = false,
                ErrorMessage = $"Missing required parameter(s): {string.Join(", ", missing)}."
            };
        }

        string stdout = string.Empty;
        string stderr = string.Empty;
        int exitCode = 0;

        if (!string.IsNullOrWhiteSpace(task.Command))
        {
            var (code, stdoutText, stderrText) = await RunProcessAsync(task, resolved, cancellationToken);
            exitCode = code;
            stdout = stdoutText;
            stderr = stderrText;

            if (exitCode != 0)
            {
                _logger.LogWarning("Task '{Task}' exited with code {Code}", task.Name, exitCode);
            }
        }

        var result = new TaskExecutionResult
        {
            Success = exitCode == 0,
            ExitCode = exitCode
        };

        try
        {
            await _output.CollectAsync(task, resolved, stdout, stderr, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect output for task '{Task}'", task.Name);
            result.Success = false;
            result.ErrorMessage = $"Output collection failed: {ex.Message}";
        }

        if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
        {
            result.ErrorMessage = string.IsNullOrWhiteSpace(stderr)
                ? $"Command exited with code {exitCode}."
                : $"Command exited with code {exitCode}:\n{stderr}";
        }

        return result;
    }

    private Dictionary<string, object?> ResolveParameters(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> supplied)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in task.Parameters)
        {
            if (supplied.TryGetValue(p.Name, out var v) && v is not null)
            {
                result[p.Name] = v;
            }
            else if (p.Default is not null)
            {
                result[p.Name] = p.Default;
            }
        }
        return result;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ParameterTemplate.Apply(task.Command!, parameters),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in ParameterTemplate.ApplyAll(task.Args, parameters))
        {
            psi.ArgumentList.Add(arg);
        }

        var workingDir = task.WorkingDirectory ?? _options.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            psi.WorkingDirectory = ParameterTemplate.Apply(workingDir, parameters);
        }

        foreach (var (k, v) in task.Env)
        {
            psi.Environment[k] = ParameterTemplate.Apply(v, parameters);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        var timeoutSeconds = task.TimeoutSeconds ?? _options.CommandTimeoutSeconds;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        _logger.LogInformation("Running task '{Task}': {Cmd} {Args}", task.Name, psi.FileName, string.Join(' ', psi.ArgumentList));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            stderrBuilder.AppendLine($"[teletasks] killed after {timeoutSeconds}s timeout");
            return (124, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }
}
