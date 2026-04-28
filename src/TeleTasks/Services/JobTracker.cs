using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

public sealed class JobTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<JobTracker> _logger;
    private readonly IOptions<ChatOptions> _options;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, JobRecord> _jobs = new();
    private int _nextId = 1;

    public string LogDirectory { get; }
    public string RegistryPath { get; }

    public JobTracker(ILogger<JobTracker> logger, IOptions<ChatOptions> options)
    {
        _logger = logger;
        _options = options;
        var configDir = UserConfigDirectory.EnsureExists();
        LogDirectory = Path.Combine(configDir, "run-logs");
        RegistryPath = Path.Combine(configDir, "jobs.json");
        Directory.CreateDirectory(LogDirectory);
        LoadAndReconcile();
        var pruned = Prune();
        if (pruned > 0)
            _logger.LogInformation("Startup pruner removed {Count} finished job(s).", pruned);
    }

    public IReadOnlyList<JobRecord> List(int max = 50)
    {
        return _jobs.Values
            .OrderByDescending(j => !j.IsFinished)
            .ThenByDescending(j => j.StartedAtUtc)
            .Take(max)
            .ToList();
    }

    public JobRecord? Get(int id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public JobRecord Start(TaskDefinition task, IReadOnlyDictionary<string, object?> parameters,
        string command, IReadOnlyList<string> args)
    {
        int id;
        lock (_gate) id = _nextId++;

        var safeName = SanitizeForFile(task.Name);
        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        var logPath = Path.Combine(LogDirectory, $"{safeName}-{id}-{ts}.log");
        var exitCodePath = Path.Combine(LogDirectory, $"{safeName}-{id}-{ts}.exitcode");

        // Compose the inner command line, properly quoted, then run it via:
        //   setsid bash -c '<inner>; echo $? > <exitcode>' >log 2>&1 < /dev/null & echo $!
        // setsid detaches from our session so the child survives bot restarts.
        var inner = string.Join(' ', new[] { command }.Concat(args).Select(ShellQuote));
        var bashScript =
            $"setsid bash -c {ShellQuote($"{inner}; echo $? > {ShellQuote(exitCodePath).Trim('\'')}")}" +
            $" >{ShellQuote(logPath)} 2>&1 < /dev/null & echo $!";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(bashScript);
        if (!string.IsNullOrWhiteSpace(task.WorkingDirectory))
        {
            psi.WorkingDirectory = task.WorkingDirectory!;
        }
        foreach (var (k, v) in task.Env)
        {
            psi.Environment[k] = ParameterTemplate.Apply(v, parameters);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn detacher shell.");

        var stdout = proc.StandardOutput.ReadToEnd().Trim();
        var stderr = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit(5000);

        if (!int.TryParse(stdout, out var pid))
        {
            throw new InvalidOperationException(
                $"Could not parse PID from spawn output. stdout='{stdout}' stderr='{stderr}'");
        }

        var record = new JobRecord
        {
            Id = id,
            TaskName = task.Name,
            Pid = pid,
            LogPath = logPath,
            ExitCodePath = exitCodePath,
            StartedAtUtc = DateTime.UtcNow,
            Task = task,
            Parameters = new Dictionary<string, object?>(parameters)
        };
        _jobs[id] = record;
        Persist();

        _logger.LogInformation(
            "Started job {Id} '{Task}' as pid {Pid}. log={Log}",
            id, task.Name, pid, logPath);
        return record;
    }

    public int Prune(bool forceAll = false)
    {
        var options = _options.Value;
        var toRemove = new HashSet<int>();

        var finishedJobs = _jobs.Values
            .Where(j => j.IsFinished)
            .OrderByDescending(j => j.StartedAtUtc)
            .ToList();

        if (forceAll)
        {
            foreach (var job in finishedJobs) toRemove.Add(job.Id);
        }
        else
        {
            var keepSet = new HashSet<int>();
            var taskGroups = finishedJobs.GroupBy(j => j.TaskName);

            foreach (var group in taskGroups)
            {
                int floorCount = 0;
                foreach (var job in group)
                {
                    bool countsTowardFloor = (job.ExitCode == 0 && !job.Killed) || options.JobRetentionKeepFailed;
                    if (countsTowardFloor && floorCount < options.JobRetentionMinPerTask)
                    {
                        keepSet.Add(job.Id);
                        floorCount++;
                    }
                }
            }

            var canPrune = finishedJobs.Where(j => !keepSet.Contains(j.Id)).ToList();
            
            // Prune based on age
            var cutoff = DateTime.UtcNow.AddDays(-options.JobRetentionDays);
            var expired = canPrune.Where(j => j.StartedAtUtc < cutoff).ToList();
            foreach (var job in expired) toRemove.Add(job.Id);

            // Enforce global max cap
            var stillIn = finishedJobs.Where(j => !toRemove.Contains(j.Id)).ToList();
            if (stillIn.Count > options.JobRetentionMaxTotal)
            {
                int excess = stillIn.Count - options.JobRetentionMaxTotal;
                var oldest = stillIn
                    .Where(j => !keepSet.Contains(j.Id))
                    .OrderBy(j => j.StartedAtUtc)
                    .Take(excess);
                foreach (var job in oldest) toRemove.Add(job.Id);
            }
        }

        int removedCount = 0;
        foreach (var id in toRemove)
        {
            if (_jobs.TryRemove(id, out var job))
            {
                removedCount++;
                DeleteJobFiles(job);
            }
        }

        if (removedCount > 0) Persist();
        return removedCount;
    }

    public JobRecord? Restart(int oldId)
    {
        if (!_jobs.TryGetValue(oldId, out var old)) return null;
        if (!old.IsFinished) return null;
        if (old.Task is null || string.IsNullOrWhiteSpace(old.Task.Command)) return null;

        var command = ParameterTemplate.Apply(old.Task.Command, old.Parameters);
        var args = ParameterTemplate.ApplyAll(old.Task.Args, old.Parameters).ToList();
        var newJob = Start(old.Task, old.Parameters, command, args);
        newJob.RestartedFromJobId = oldId;
        Persist();
        return newJob;
    }

    private void DeleteJobFiles(JobRecord job)
    {
        try { if (File.Exists(job.LogPath)) File.Delete(job.LogPath); } catch { }
        try { if (File.Exists(job.ExitCodePath)) File.Delete(job.ExitCodePath); } catch { }
    }

    public bool Stop(int id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.IsFinished) return false;

        var alreadyGone = false;
        try
        {
            using var p = Process.GetProcessById(job.Pid);
            // Best effort: kill the whole process tree (the detached shell + children).
            try { p.Kill(entireProcessTree: true); }
            catch { p.Kill(); }
            _logger.LogInformation("Sent kill to job {Id} (pid {Pid}).", id, job.Pid);
        }
        catch (ArgumentException)
        {
            // Process didn't exist when we looked it up — already exited on its own.
            alreadyGone = true;
            _logger.LogInformation("Job {Id} pid {Pid} already exited.", id, job.Pid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill job {Id} pid {Pid}", id, job.Pid);
            return false;
        }

        // Verify the kill actually took. setsid put the wrapper in its own session,
        // so .NET's process-tree walk can miss grandchildren. If it's still alive
        // after a brief grace period, send SIGKILL to the whole session group.
        if (!alreadyGone && IsAlive(job.Pid))
        {
            for (var i = 0; i < 10 && IsAlive(job.Pid); i++)
            {
                Thread.Sleep(100);
            }
            if (IsAlive(job.Pid))
            {
                _logger.LogWarning(
                    "Job {Id} pid {Pid} still alive after Process.Kill; escalating to kill -KILL -{Pid}.",
                    id, job.Pid, job.Pid);
                TryKillSession(job.Pid);
                for (var i = 0; i < 10 && IsAlive(job.Pid); i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        if (IsAlive(job.Pid))
        {
            _logger.LogWarning("Job {Id} pid {Pid} did not exit after escalation.", id, job.Pid);
            return false;
        }

        // Mark finished now rather than waiting for the next Reconcile — the wrapper
        // may briefly become a zombie before init reaps it, and the user expects the
        // /stop reply to reflect "stopped" immediately. We tag Killed so the UI can
        // say "killed" rather than the misleading "exit unknown" — the wrapper
        // never got to write its $? to the exitcode file because we SIGKILLed it.
        job.FinishedAtUtc ??= DateTime.UtcNow;
        if (!alreadyGone) job.Killed = true;
        Persist();
        return true;
    }

    private void TryKillSession(int pid)
    {
        // setsid made <pid> the session leader → its session ID equals its pid,
        // so kill -KILL -<pid> reaches every process in the session.
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                ArgumentList = { "-KILL", $"-{pid}" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "kill -KILL -{Pid} failed", pid);
        }
    }

    /// <summary>
    /// Re-check the running jobs and update finished state. Cheap; the bot calls this
    /// on demand (e.g. before responding to /jobs or /job N) so we don't need a timer.
    /// </summary>
    public void Refresh()
    {
        var changed = false;
        foreach (var job in _jobs.Values.Where(j => !j.IsFinished).ToList())
        {
            if (Reconcile(job)) changed = true;
        }
        if (changed) Persist();
    }

    public void AssignChat(int id, ChatId chatId)
    {
        if (!_jobs.TryGetValue(id, out var job)) return;
        if (job.ChatId == chatId) return;
        job.ChatId = chatId;
        Persist();
    }

    public void RecordSeenArtifacts(int id, IEnumerable<string> paths)
    {
        if (!_jobs.TryGetValue(id, out var job)) return;
        var added = false;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (!job.SeenArtifactPaths.Contains(p, StringComparer.Ordinal))
            {
                job.SeenArtifactPaths.Add(p);
                added = true;
            }
        }
        if (added) Persist();
    }

    public void MarkCompletionNotified(int id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return;
        if (job.CompletionNotified) return;
        job.CompletionNotified = true;
        Persist();
    }

    public string TailLog(int id, int lines)
    {
        if (!_jobs.TryGetValue(id, out var job) || !File.Exists(job.LogPath))
            return string.Empty;
        return TailFile(job.LogPath, lines);
    }

    private bool Reconcile(JobRecord job)
    {
        if (job.IsFinished) return false;
        if (!IsAlive(job.Pid))
        {
            job.FinishedAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(job.ExitCodePath) && File.Exists(job.ExitCodePath))
            {
                try
                {
                    var text = File.ReadAllText(job.ExitCodePath).Trim();
                    if (int.TryParse(text, out var code)) job.ExitCode = code;
                }
                catch { }
            }
            return true;
        }
        return false;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            // /proc/<pid>/stat persists for zombies, so Process.GetProcessById /
            // HasExited would say "alive" for a process that's already done. Read
            // the state field directly: 'Z' = zombie, 'X' = dead, anything else
            // (R/S/D/T) is genuinely alive.
            var statPath = $"/proc/{pid}/stat";
            if (!File.Exists(statPath)) return false;
            var stat = File.ReadAllText(statPath);
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0 || lastParen + 2 >= stat.Length) return false;
            var state = stat[lastParen + 2];
            return state != 'Z' && state != 'X';
        }
        catch { return false; }
    }

    private void LoadAndReconcile()
    {
        if (!File.Exists(RegistryPath)) return;
        try
        {
            using var fs = File.OpenRead(RegistryPath);
            var registry = JsonSerializer.Deserialize<JobRegistry>(fs, JsonOptions);
            if (registry is null) return;
            _nextId = Math.Max(_nextId, registry.NextId);
            foreach (var job in registry.Jobs)
            {
                Reconcile(job);
                _jobs[job.Id] = job;
                if (job.Id >= _nextId) _nextId = job.Id + 1;
            }
            _logger.LogInformation(
                "Loaded {Count} job record(s) from {Path} ({Active} still running).",
                _jobs.Count, RegistryPath, _jobs.Values.Count(j => !j.IsFinished));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load job registry at {Path}", RegistryPath);
        }
    }

    private void Persist()
    {
        try
        {
            var registry = new JobRegistry
            {
                NextId = _nextId,
                Jobs = _jobs.Values.OrderBy(j => j.Id).ToList()
            };
            var tmp = RegistryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(registry, JsonOptions));
            File.Move(tmp, RegistryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist job registry at {Path}", RegistryPath);
        }
    }

    private static string SanitizeForFile(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string TailFile(string path, int lines)
    {
        if (lines <= 0) return string.Empty;
        try
        {
            var queue = new Queue<string>(lines);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (queue.Count == lines) queue.Dequeue();
                queue.Enqueue(line);
            }
            return string.Join('\n', queue);
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }
}
