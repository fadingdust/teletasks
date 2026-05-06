using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

/// <summary>
/// Periodically walks the job registry and pushes new artifacts +
/// completion summaries to the originating chat. Lives as a free-standing
/// hosted service so a single process can host multiple
/// <see cref="IChatProvider"/>s and dispatch each job's pushes to the
/// right one via <see cref="JobRecord.ChatId"/>.<c>Provider</c>.
///
/// Lookup is built once at start: <c>provider.Name</c> → instance. A job
/// whose <c>ChatId.Provider</c> isn't registered gets one warning logged
/// (deduplicated by provider name) and is then skipped silently.
/// </summary>
public sealed class JobNotifierService : BackgroundService
{
    private readonly ChatOptions _options;
    private readonly JobTracker _jobs;
    private readonly TaskExecutor _executor;
    private readonly ChatResultDispatcher _dispatcher;
    private readonly Dictionary<string, IChatProvider> _providersByName;
    private readonly HashSet<string> _warnedAboutProvider = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<JobNotifierService> _logger;

    public JobNotifierService(
        IOptions<ChatOptions> options,
        IEnumerable<IChatProvider> providers,
        JobTracker jobs,
        TaskExecutor executor,
        ChatResultDispatcher dispatcher,
        ILogger<JobNotifierService> logger)
    {
        _options = options.Value;
        _jobs = jobs;
        _executor = executor;
        _dispatcher = dispatcher;
        _logger = logger;
        _providersByName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = _options.JobPollSeconds;
        if (pollSeconds <= 0)
        {
            _logger.LogInformation("Job poll disabled (Chat:JobPollSeconds <= 0). Sleeping forever.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        var interval = TimeSpan.FromSeconds(pollSeconds);
        _logger.LogInformation("Job notifier loop running every {Seconds}s.", pollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job notifier iteration failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        _jobs.Refresh();
        // List() returns active first, then finished by recency; cap at 50.
        // Finished+already-notified jobs are no-ops below, so the cap is fine.
        foreach (var job in _jobs.List(50))
        {
            if (job.ChatId is not { } chatRef) continue;
            if (job.Task is null) continue;

            if (!_providersByName.TryGetValue(chatRef.Provider, out var provider))
            {
                if (_warnedAboutProvider.Add(chatRef.Provider))
                {
                    _logger.LogWarning(
                        "Job {Id} originated from provider '{Provider}' which is not registered; skipping its pushes.",
                        job.Id, chatRef.Provider);
                }
                continue;
            }

            var output = job.Task.Output;
            // Progressive push runs while the job is alive, plus one final
            // flush after it finishes (so we catch the last artifacts before
            // the completion summary). After CompletionNotified flips true
            // we stop polling this job — otherwise a finished job whose
            // output dir is shared with a later run would keep "discovering"
            // and re-pushing the new run's artifacts as if they were its own.
            var allowProgressive = !job.IsFinished || !job.CompletionNotified;
            if (allowProgressive && output is not null && output.Type is not TaskOutputType.Text)
            {
                await PushNewArtifactsAsync(provider, chatRef, job, ct);
            }

            if (job.IsFinished && !job.CompletionNotified)
            {
                await PushCompletionAsync(provider, chatRef, job, ct);
            }
        }
    }

    private async Task PushNewArtifactsAsync(IChatProvider provider, ChatId chat, JobRecord job, CancellationToken ct)
    {
        TaskExecutionResult result;
        try
        {
            result = await _executor.EvaluateOutputAsync(job.Task!, job.Parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Progressive evaluate failed for job {Id}", job.Id);
            return;
        }

        var seen = new HashSet<string>(job.SeenArtifactPaths, StringComparer.Ordinal);
        // For a finished job, cap the mtime window so we don't claim a later
        // job's artifacts during the final flush. The grace lets late writes
        // (subprocesses still flushing after the wrapper exited) through.
        var endCutoff = job.FinishedAtUtc?.AddSeconds(10);
        var fresh = new List<OutputArtifact>();
        foreach (var artifact in result.Artifacts)
        {
            if (string.IsNullOrEmpty(artifact.Path)) continue; // text-only: skip
            if (seen.Contains(artifact.Path)) continue;
            try
            {
                // mtime stable for at least one tick → file is settled. Anything
                // older than the job's start belongs to a previous run; anything
                // newer than its finish (plus grace) belongs to a later run.
                var mtime = File.GetLastWriteTimeUtc(artifact.Path);
                if (mtime < job.StartedAtUtc) continue;
                if (endCutoff is DateTime cut && mtime > cut) continue;
            }
            catch { continue; }
            fresh.Add(artifact);
        }
        if (fresh.Count == 0) return;

        // Unsolicited pushes need to identify the job — when an image lands in
        // chat 30s after you asked, you want to know which run produced it
        // without scrolling back. Prepend a one-line tag to each caption.
        var jobTag = $"Job {job.Id} • {job.TaskName}";
        var bundle = new TaskExecutionResult { Success = true };
        foreach (var a in fresh)
        {
            var caption = string.IsNullOrEmpty(a.Caption) ? jobTag : $"{jobTag}\n{a.Caption}";
            bundle.Artifacts.Add(a with { Caption = caption });
        }
        try
        {
            await _dispatcher.DispatchAsync(provider, chat, bundle, ct);
            _jobs.RecordSeenArtifacts(job.Id,
                fresh.Where(a => !string.IsNullOrEmpty(a.Path)).Select(a => a.Path!));
            _logger.LogInformation("Pushed {Count} new artifact(s) for job {Id}.", fresh.Count, job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push new artifacts for job {Id}; will retry next poll.", job.Id);
        }
    }

    private async Task PushCompletionAsync(IChatProvider provider, ChatId chat, JobRecord job, CancellationToken ct)
    {
        var summary = new StringBuilder();
        summary.Append("✅ Job ").Append(job.Id).Append(" <code>")
               .Append(Escape(job.TaskName)).Append("</code> ")
               .Append(Escape(FormatJobExit(job)))
               .Append(" after ").Append(Escape(FormatElapsed(job.Elapsed))).Append('.');

        // Tap-actions for the moment a job ends: [Job N] for output / log tail,
        // [Restart N] only when there's a stored task definition (otherwise the
        // /restart handler refuses with "no stored task definition").
        var row = new List<InlineButton> { new($"Job {job.Id}", $"/job {job.Id}") };
        if (job.Task is { Command: { Length: > 0 } })
            row.Add(new InlineButton($"Restart {job.Id}", $"/restart {job.Id}"));
        var keyboard = new IReadOnlyList<InlineButton>[] { row };

        try
        {
            await provider.SendHtmlAsync(chat, summary.ToString(), keyboard, ct);
            _jobs.MarkCompletionNotified(job.Id);
            _logger.LogInformation("Pushed completion summary for job {Id}.", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push completion for job {Id}; will retry next poll.", job.Id);
        }
    }

    private static string FormatJobExit(JobRecord j)
    {
        if (j.Killed) return "killed";
        if (j.ExitCode is int code) return code == 0 ? "ok" : $"exit {code}";
        return "exit unknown";
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m {(int)(span.TotalSeconds % 60)}s";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours}h {(int)(span.TotalMinutes % 60)}m";
        return $"{(int)span.TotalDays}d {(int)(span.TotalHours % 24)}h";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
