using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

public sealed class TelegramBotService : BackgroundService
{
    private readonly TelegramOptions _options;
    private readonly IChatProvider _provider;
    private readonly ChatResultDispatcher _dispatcher;
    private readonly TaskRegistry _registry;
    private readonly TaskMatcher _matcher;
    private readonly TaskExecutor _executor;
    private readonly OllamaClient _ollama;
    private readonly OutputCollector _output;
    private readonly JobTracker _jobs;
    private readonly ConversationStateTracker _conversation;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        IOptions<TelegramOptions> options,
        IChatProvider provider,
        ChatResultDispatcher dispatcher,
        TaskRegistry registry,
        TaskMatcher matcher,
        TaskExecutor executor,
        OllamaClient ollama,
        OutputCollector output,
        JobTracker jobs,
        ConversationStateTracker conversation,
        ILogger<TelegramBotService> logger)
    {
        _options = options.Value;
        _provider = provider;
        _dispatcher = dispatcher;
        _registry = registry;
        _matcher = matcher;
        _executor = executor;
        _ollama = ollama;
        _output = output;
        _jobs = jobs;
        _conversation = conversation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogError("Telegram:Token not configured. Bot will not start.");
            return;
        }

        _registry.Load();
        await _provider.StartAsync(stoppingToken);
        _provider.OnMessage += OnIncomingAsync;
        _logger.LogInformation("Loaded {Tasks} task(s).", _registry.Tasks.Count);

        await CheckOllamaHealthAndNotifyAsync(stoppingToken);

        try
        {
            await RunJobNotifierLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Periodically walks the job registry and, for jobs that originated from
    /// a chat, pushes:
    ///   - any new output artifacts since the last poll (progressive)
    ///   - a one-time completion summary when the job transitions to finished
    /// Runs in the same task as ExecuteAsync; the bot's OnMessage handler
    /// fires independently on its own thread, so user commands aren't blocked.
    /// </summary>
    private async Task RunJobNotifierLoopAsync(CancellationToken ct)
    {
        var pollSeconds = _options.JobPollSeconds;
        if (pollSeconds <= 0)
        {
            _logger.LogInformation("Job poll disabled (Telegram:JobPollSeconds <= 0). Sleeping forever.");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        var interval = TimeSpan.FromSeconds(pollSeconds);
        _logger.LogInformation("Job notifier loop running every {Seconds}s.", pollSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollJobsOnceAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job notifier iteration failed");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollJobsOnceAsync(CancellationToken ct)
    {
        _jobs.Refresh();
        // List() returns active first, then finished by recency; cap at 50.
        // Finished+already-notified jobs are no-ops below, so the cap is fine.
        foreach (var job in _jobs.List(50))
        {
            if (job.ChatId is not { } chatRef) continue;
            if (!long.TryParse(chatRef.Id, out var chatId)) continue;
            if (job.Task is null) continue;

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
                await PushNewArtifactsAsync(chatId, job, ct);
            }

            if (job.IsFinished && !job.CompletionNotified)
            {
                await PushCompletionAsync(chatId, job, ct);
            }
        }
    }

    private async Task PushNewArtifactsAsync(long chatId, JobRecord job, CancellationToken ct)
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
            await _dispatcher.DispatchAsync(_provider, ChatId.FromTelegram(chatId), bundle, ct);
            _jobs.RecordSeenArtifacts(job.Id,
                fresh.Where(a => !string.IsNullOrEmpty(a.Path)).Select(a => a.Path!));
            _logger.LogInformation("Pushed {Count} new artifact(s) for job {Id}.", fresh.Count, job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push new artifacts for job {Id}; will retry next poll.", job.Id);
        }
    }

    private async Task PushCompletionAsync(long chatId, JobRecord job, CancellationToken ct)
    {
        var summary = new StringBuilder();
        summary.Append("✅ Job ").Append(job.Id).Append(" <code>")
               .Append(HtmlEscape(job.TaskName)).Append("</code> ")
               .Append(HtmlEscape(FormatJobExit(job)))
               .Append(" after ").Append(HtmlEscape(FormatElapsed(job.Elapsed))).Append('.');
        try
        {
            await _provider.SendHtmlAsync(ChatId.FromTelegram(chatId), summary.ToString(), ct);
            _jobs.MarkCompletionNotified(job.Id);
            _logger.LogInformation("Pushed completion summary for job {Id}.", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push completion for job {Id}; will retry next poll.", job.Id);
        }
    }

    private async Task CheckOllamaHealthAndNotifyAsync(CancellationToken cancellationToken)
    {
        string? warning = null;
        try
        {
            var models = await _ollama.ListModelsAsync(cancellationToken);
            if (models.Count == 0)
            {
                warning =
                    $"⚠️ I'm online, but Ollama at <code>{HtmlEscape(_ollama.ConfiguredEndpoint)}</code> " +
                    "reports no installed models.\n\n" +
                    $"On the host machine run:\n<pre>ollama pull {HtmlEscape(_ollama.ConfiguredModel)}</pre>";
                _logger.LogWarning("Ollama is reachable but has no models pulled.");
            }
            else if (!models.Contains(_ollama.ConfiguredModel, StringComparer.OrdinalIgnoreCase))
            {
                warning =
                    $"⚠️ I'm online, but Ollama doesn't have model <code>{HtmlEscape(_ollama.ConfiguredModel)}</code> pulled.\n\n" +
                    $"Available: <code>{HtmlEscape(string.Join(", ", models.Take(8)))}</code>\n\n" +
                    $"On the host machine run:\n<pre>ollama pull {HtmlEscape(_ollama.ConfiguredModel)}</pre>";
                _logger.LogWarning("Configured Ollama model '{Model}' is not pulled. Available: {Models}",
                    _ollama.ConfiguredModel, string.Join(", ", models));
            }
            else
            {
                _logger.LogInformation("Ollama health: ok ({Model} pulled, {Count} model(s) available).",
                    _ollama.ConfiguredModel, models.Count);
            }
        }
        catch (OllamaUnreachableException ex)
        {
            warning =
                $"⚠️ I'm online, but I can't reach Ollama at <code>{HtmlEscape(_ollama.ConfiguredEndpoint)}</code>.\n\n" +
                $"<pre>{HtmlEscape(ex.Message)}</pre>\n\n" +
                "Start it with:\n<pre>ollama serve</pre>";
            _logger.LogWarning(ex, "Ollama is unreachable at startup.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama health check failed unexpectedly.");
        }

        if (warning is not null)
        {
            await SendStartupNotificationAsync(warning, cancellationToken);
        }
    }

    private async Task SendStartupNotificationAsync(string htmlBody, CancellationToken cancellationToken)
    {
        if (!_options.StartupNotificationsEnabled) return;

        // Provider.DefaultRecipient picks the first allow-listed user/chat in
        // a Telegram-shaped allow-list today, but the abstraction lets a future
        // Discord provider supply its own primary recipient (typically the
        // first AllowedUserId for DM-only deployments).
        var recipient = _provider.DefaultRecipient;
        if (recipient is null)
        {
            _logger.LogWarning("Startup notification not sent: provider has no default recipient.");
            return;
        }

        try
        {
            await _provider.SendHtmlAsync(recipient.Value, htmlBody, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send startup notification to {Recipient}", recipient);
        }
    }

    private async Task OnIncomingAsync(IncomingMessage message)
    {
        var ct = CancellationToken.None;

        var text = message.Text;
        if (string.IsNullOrEmpty(text)) return;

        // Provider hands us provider-native ids as strings; adapt back to longs
        // so the existing handler body stays untouched. The 2d.3 allow-list-
        // delegation step deletes this adapter when the host stops caring about
        // the underlying id type.
        if (!long.TryParse(message.UserId, out var userId)) userId = 0;
        if (!long.TryParse(message.Chat.Id, out var chatId))
        {
            _logger.LogWarning("Non-Telegram chat id {ChatId} routed to Telegram host; dropping.", message.Chat);
            return;
        }
        var chat = message.Chat;
        var username = message.Username;

        if (!IsAuthorized(userId, chatId))
        {
            _logger.LogWarning("Unauthorized message from {User} ({UserId}) chat {ChatId}", username, userId, chatId);
            await _provider.SendTextAsync(chat, "Not authorized.", ct);
            return;
        }

        _logger.LogInformation("Message from {User} ({UserId}): {Text}", username, userId, text);

        try
        {
            // If we're in the middle of collecting parameters from this user, the
            // next message is the value for the current parameter — UNLESS it's
            // a real slash command (in which case we cancel the conversation
            // and route the command). A user-typed path like /var/log/syslog
            // looks slash-command-shaped but is actually their answer.
            var pending = _conversation.Get(chat);
            var isCommand = SlashCommand.IsCommand(text);
            if (pending is not null && !isCommand)
            {
                await ContinueParameterCollectionAsync(chatId, pending, text, ct);
                return;
            }
            if (isCommand && pending is not null)
            {
                _conversation.Clear(chat);
                await _provider.SendTextAsync(chat,
                    $"Cancelled the pending {pending.Task.Name}.",
                    ct);
                if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase)) return;
                // Fall through so other slash commands still work.
            }

            var dryRun = false;
            var routedText = text;
            if (text.StartsWith("/dry", StringComparison.OrdinalIgnoreCase) &&
                (text.Length == 4 || char.IsWhiteSpace(text[4])))
            {
                dryRun = true;
                routedText = text.Length > 4 ? text[4..].TrimStart() : string.Empty;
                if (string.IsNullOrWhiteSpace(routedText))
                {
                    await _provider.SendTextAsync(chat, "Usage: /dry <natural-language request>", ct);
                    return;
                }
            }
            else if (isCommand)
            {
                await HandleCommandAsync(chatId, text, ct);
                return;
            }

            await _provider.SendTypingAsync(chat, ct);

            // Fast path: when the user types exactly a task name, skip the
            // LLM call entirely. Saves 20-30s on tiny models, avoids any
            // chance of parameter hallucination, and the conversational
            // prompt loop fills in every required parameter from scratch.
            TaskMatch? match;
            var trimmed = routedText.Trim();
            var directHit = _registry.Find(trimmed);
            if (directHit is not null)
            {
                match = new TaskMatch(directHit.Name, new Dictionary<string, object?>(), "exact task-name match");
            }
            else
            {
                match = await _matcher.MatchAsync(routedText, ct);
            }
            if (match is null || string.IsNullOrEmpty(match.TaskName))
            {
                var reason = match?.Reasoning;
                var reply = string.IsNullOrWhiteSpace(reason)
                    ? "I couldn't find a task that matches that. Try /tasks to see what I can do."
                    : $"No matching task: {reason}";
                await _provider.SendTextAsync(chat, reply, ct);
                return;
            }

            if (match.TaskName == TaskMatcher.ShowTasksRoute)
            {
                await _provider.SendTextAsync(chat, BuildTaskList(), ct);
                return;
            }
            if (match.TaskName == TaskMatcher.ShowHelpRoute)
            {
                await _provider.SendTextAsync(chat, BuildHelp(), ct);
                return;
            }
            if (match.TaskName == TaskMatcher.ShowResultsRoute)
            {
                var requested = match.Parameters.TryGetValue("task_name", out var tn) ? tn?.ToString() : null;
                await SendResultsAsync(chatId, requested, ct);
                return;
            }
            if (match.TaskName == TaskMatcher.ShowJobsRoute)
            {
                await SendJobsListAsync(chatId, ct);
                return;
            }
            if (match.TaskName == TaskMatcher.CheckLatestJobRoute)
            {
                await SendLatestJobStatusAsync(chatId, ct);
                return;
            }

            var task = _registry.Find(match.TaskName)!;

            if (dryRun)
            {
                await _provider.SendHtmlAsync(chat, RenderDryRun(task, match.Parameters), ct);
                return;
            }

            var missingRequired = task.Parameters
                .Where(p => p.Required && !MissingValueGuard.HasUsableValue(p, match.Parameters, routedText, task.Name))
                .ToList();
            if (missingRequired.Count > 0)
            {
                var state = _conversation.Begin(chat, task, match.Parameters, missingRequired);
                await _provider.SendHtmlAsync(chat,
                    $"→ <code>{HtmlEscape(task.Name)}</code> needs {missingRequired.Count} more value(s). " +
                    "Send each one in turn, or /cancel to abort.",
                    ct);
                await PromptNextParameterAsync(chatId, state, ct);
                return;
            }

            await _provider.SendHtmlAsync(chat,
                $"→ Running <code>{HtmlEscape(task.Name)}</code>{HtmlEscape(FormatParameterList(match.Parameters))}",
                ct);

            var result = await _executor.ExecuteAsync(task, match.Parameters, ct);
            // Bind the originating chat to this job so the notifier loop knows
            // where to push new artifacts and the completion summary.
            if (result.JobId is int newJobId)
            {
                _jobs.AssignChat(newJobId, ChatId.FromTelegram(chatId));
            }
            await _dispatcher.DispatchAsync(_provider, ChatId.FromTelegram(chatId), result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleCommandAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);
        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        // @MyBot mention stripping happens upstream in TelegramChatProvider
        // before OnMessage fires, so head never contains '@' here.

        switch (head.ToLowerInvariant())
        {
            case "/start":
            case "/help":
                await _provider.SendTextAsync(chat, BuildHelp(), cancellationToken);
                break;
            case "/tasks":
                await _provider.SendTextAsync(chat, BuildTaskList(), cancellationToken);
                break;
            case "/reload":
                _registry.Load();
                await _provider.SendTextAsync(chat, $"Reloaded {_registry.Tasks.Count} task(s).", cancellationToken);
                break;
            case "/whoami":
                await _provider.SendTextAsync(chat, $"chat={chatId}", cancellationToken);
                break;
            case "/results":
                {
                    var arg = space < 0 ? null : text[(space + 1)..].Trim();
                    await SendResultsAsync(chatId, arg, cancellationToken);
                    break;
                }
            case "/jobs":
                await SendJobsListAsync(chatId, cancellationToken);
                break;
            case "/job":
                await HandleJobCommandAsync(chatId, text, cancellationToken);
                break;
            case "/stop":
                await HandleStopCommandAsync(chatId, text, cancellationToken);
                break;
            case "/cancel":
                // The OnIncomingAsync entry path already cleared any pending state
                // when a slash command arrived; this branch just acknowledges.
                await _provider.SendTextAsync(chat, "Nothing pending.", cancellationToken);
                break;
            default:
                await _provider.SendTextAsync(chat, "Unknown command. Try /help.", cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Look up the named task and re-evaluate its output spec against the
    /// current state of disk WITHOUT running the command. For Images / File
    /// / LogTail outputs this just reads what's already there. For Text
    /// outputs (which need stdout from a command) we tell the user to run
    /// the task instead.
    /// </summary>
    private async Task SendResultsAsync(long chatId, string? requestedName, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            await _provider.SendTextAsync(chat,
                "Usage: /results <task-name>. See /tasks for the list.",
                cancellationToken);
            return;
        }

        var task = _registry.Find(requestedName);
        if (task is null)
        {
            var disabled = _registry.DisabledTasks.FirstOrDefault(t =>
                string.Equals(t.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            var hint = disabled is not null ? " (it's disabled — flip enabled:true to use)" : "";
            await _provider.SendHtmlAsync(chat,
                $"No task named <code>{HtmlEscape(requestedName)}</code>{hint}.",
                cancellationToken);
            return;
        }

        if (task.Output.Type == TaskOutputType.Text)
        {
            await _provider.SendHtmlAsync(chat,
                $"<code>{HtmlEscape(task.Name)}</code> has Text output (its stdout). " +
                "There's no cached state on disk to show — run the task to see results.",
                cancellationToken);
            return;
        }

        TaskExecutionResult result;
        try
        {
            result = await _executor.EvaluateOutputAsync(task, parameters: null, cancellationToken);
        }
        catch (Exception ex)
        {
            await _provider.SendHtmlAsync(chat,
                $"Could not read latest output for <code>{HtmlEscape(task.Name)}</code>: {HtmlEscape(ex.Message)}",
                cancellationToken);
            return;
        }

        if (result.Artifacts.Count == 0)
        {
            await _provider.SendHtmlAsync(chat,
                $"No output yet for <code>{HtmlEscape(task.Name)}</code>.",
                cancellationToken);
            return;
        }

        await _dispatcher.DispatchAsync(_provider, ChatId.FromTelegram(chatId), result, cancellationToken);
    }

    private async Task HandleJobCommandAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var id))
        {
            await SendJobsListAsync(chatId, cancellationToken);
            return;
        }
        await SendJobStatusAsync(chatId, id, cancellationToken);
    }

    private async Task HandleStopCommandAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var id))
        {
            await _provider.SendTextAsync(chat, "Usage: /stop <job-id>. See /jobs for IDs.", cancellationToken);
            return;
        }

        var job = _jobs.Get(id);
        if (job is null)
        {
            await _provider.SendTextAsync(chat, $"No job with id {id}.", cancellationToken);
            return;
        }
        if (job.IsFinished)
        {
            await _provider.SendTextAsync(chat, $"Job {id} ({job.TaskName}) is already finished.", cancellationToken);
            return;
        }

        var stopped = _jobs.Stop(id);
        await _provider.SendTextAsync(chat,
            stopped
                ? $"Sent kill to job {id} ({job.TaskName}, pid {job.Pid})."
                : $"Could not stop job {id}. See logs.",
            cancellationToken);
    }

    private async Task SendJobsListAsync(long chatId, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);
        _jobs.Refresh();
        var jobs = _jobs.List();
        if (jobs.Count == 0)
        {
            await _provider.SendHtmlAsync(chat, "No jobs yet. Tasks with <code>longRunning: true</code> show up here once started.",
                cancellationToken);
            return;
        }

        var sb = new StringBuilder();
        var active = jobs.Where(j => !j.IsFinished).ToList();
        var finished = jobs.Where(j => j.IsFinished).Take(10).ToList();

        if (active.Count > 0)
        {
            sb.AppendLine("<b>Active</b>:");
            foreach (var j in active)
            {
                sb.Append("• /job ").Append(j.Id).Append(" — <code>")
                  .Append(HtmlEscape(j.TaskName)).Append("</code> running ")
                  .Append(HtmlEscape(FormatElapsed(j.Elapsed))).AppendLine();
            }
        }
        if (finished.Count > 0)
        {
            if (active.Count > 0) sb.AppendLine();
            sb.AppendLine("<b>Recent</b>:");
            foreach (var j in finished)
            {
                var exit = FormatJobExit(j);
                sb.Append("• /job ").Append(j.Id).Append(" — <code>")
                  .Append(HtmlEscape(j.TaskName)).Append("</code> ")
                  .Append(HtmlEscape(exit)).Append(" after ")
                  .Append(HtmlEscape(FormatElapsed(j.Elapsed))).AppendLine();
            }
        }

        await _provider.SendHtmlAsync(ChatId.FromTelegram(chatId), sb.ToString(), cancellationToken);
    }

    private async Task SendLatestJobStatusAsync(long chatId, CancellationToken cancellationToken)
    {
        _jobs.Refresh();
        var latest = _jobs.List(50)
            .OrderByDescending(j => !j.IsFinished)
            .ThenByDescending(j => j.StartedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            await _provider.SendTextAsync(ChatId.FromTelegram(chatId), "No jobs yet.", cancellationToken);
            return;
        }
        await SendJobStatusAsync(chatId, latest.Id, cancellationToken);
    }

    private async Task SendJobStatusAsync(long chatId, int id, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);
        _jobs.Refresh();
        var job = _jobs.Get(id);
        if (job is null)
        {
            await _provider.SendTextAsync(chat, $"No job with id {id}.", cancellationToken);
            return;
        }

        var header = new StringBuilder();
        header.Append("<b>Job ").Append(job.Id).Append("</b>: <code>")
              .Append(HtmlEscape(job.TaskName)).Append("</code>\n");
        if (job.IsFinished)
        {
            var exit = FormatJobExit(job);
            header.Append(HtmlEscape($"finished {FormatElapsed(job.Elapsed)} ago, {exit}"))
                  .Append('\n');
        }
        else
        {
            header.Append("running for ").Append(HtmlEscape(FormatElapsed(job.Elapsed)))
                  .Append(" (pid ").Append(job.Pid).Append(")\n");
        }
        header.Append("log: <code>").Append(HtmlEscape(job.LogPath)).Append("</code>");
        await _provider.SendHtmlAsync(chat, header.ToString(), cancellationToken);

        var tail = _jobs.TailLog(id, 30);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            await _provider.SendHtmlAsync(chat,
                $"<b>Log tail</b>\n<pre>{HtmlEscape(tail)}</pre>",
                cancellationToken);
        }
        else if (File.Exists(job.LogPath) && !job.IsFinished)
        {
            // Empty log on a still-running job almost always means Python (or similar)
            // is block-buffering stdout because it's redirected to a file. Surface this
            // explicitly — silence here is the worst UX.
            await _provider.SendHtmlAsync(chat,
                "Log is empty so far. If this is a Python script, stdout is likely block-buffered " +
                "when redirected. Re-run with <code>PYTHONUNBUFFERED=1</code> in the task's env, " +
                "or invoke <code>python -u</code> / <code>stdbuf -oL python …</code>.",
                cancellationToken);
        }

        // Re-evaluate the task's original output spec so an Images-output task surfaces
        // its latest renders, a File-output task sends the latest file, etc. Uses the
        // shared EvaluateOutputAsync helper that /results also calls — same path,
        // same caption / glob / sidecar handling.
        if (job.Task is not null && job.Task.Output is { } outputSpec &&
            outputSpec.Type is not TaskOutputType.Text)
        {
            try
            {
                var result = await _executor.EvaluateOutputAsync(job.Task, job.Parameters, cancellationToken);
                // Drop artifacts older than the job's start — those belong to a previous
                // run and showing them under "Job N" would mislead the user into
                // thinking this job has already produced output.
                var fresh = FilterArtifactsSince(result.Artifacts, job.StartedAtUtc);
                if (fresh.Count > 0)
                {
                    var freshResult = new TaskExecutionResult { Success = result.Success, ExitCode = result.ExitCode };
                    foreach (var a in fresh) freshResult.Artifacts.Add(a);
                    await _dispatcher.DispatchAsync(_provider, ChatId.FromTelegram(chatId), freshResult, cancellationToken);
                }
                else if (job.IsFinished)
                {
                    await _provider.SendTextAsync(chat,
                        "No new outputs were produced by this job.",
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not collect current output for job {Id}", id);
            }
        }
    }

    private static List<OutputArtifact> FilterArtifactsSince(IEnumerable<OutputArtifact> artifacts, DateTime sinceUtc)
    {
        var fresh = new List<OutputArtifact>();
        foreach (var a in artifacts)
        {
            // Text-only artifacts (LogTail bodies, etc.) have no path — always keep them,
            // they're synthesized fresh on every evaluate.
            if (string.IsNullOrEmpty(a.Path))
            {
                fresh.Add(a);
                continue;
            }
            try
            {
                var mtime = File.GetLastWriteTimeUtc(a.Path);
                if (mtime >= sinceUtc) fresh.Add(a);
            }
            catch
            {
                // Path no longer accessible — drop it; nothing useful to show.
            }
        }
        return fresh;
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

    private string BuildHelp() =>
        "TeleTasks - chat in natural language to run pre-defined tasks.\n\n" +
        "Commands:\n" +
        "  /tasks          - list configured (and disabled) tasks\n" +
        "  /reload         - reload tasks.json\n" +
        "  /dry <text>     - resolve a task and show what would run, without running it\n" +
        "  /results <task> - show the latest output of <task> without running it\n" +
        "  /jobs           - list active and recent long-running jobs\n" +
        "  /job N          - status, log tail, and current output for job N\n" +
        "  /stop N         - kill a running job\n" +
        "  /cancel         - abort a pending parameter-collection prompt\n" +
        "  /whoami         - show your user/chat IDs\n" +
        "  /help           - this message\n\n" +
        "If a task needs values you didn't supply, I'll ask for them one at a time.\n" +
        "Anything else is sent to the local LLM for matching.";

    private string BuildTaskList()
    {
        if (_registry.Tasks.Count == 0 && _registry.DisabledTasks.Count == 0)
            return "No tasks configured.";

        var sb = new StringBuilder();
        sb.AppendLine("Available tasks:");
        foreach (var t in _registry.Tasks)
        {
            sb.Append("• ").Append(t.Name);
            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                sb.Append(" - ").Append(t.Description);
            }
            sb.AppendLine();
        }

        if (_registry.DisabledTasks.Count > 0)
        {
            sb.AppendLine();
            sb.Append("Disabled (").Append(_registry.DisabledTasks.Count).AppendLine("):");
            foreach (var t in _registry.DisabledTasks)
            {
                sb.Append("• ").Append(t.Name).AppendLine();
            }
        }
        return sb.ToString();
    }

    private async Task PromptNextParameterAsync(long chatId, PendingTaskState state, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);
        var p = state.Remaining.Peek();

        var sb = new StringBuilder();
        sb.Append("What's the value for <code>").Append(HtmlEscape(p.Name)).Append("</code>");
        sb.Append(" (").Append(HtmlEscape(p.Type)).Append(")?");
        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            sb.Append("\n<i>").Append(HtmlEscape(p.Description)).Append("</i>");
        }
        if (p.Enum is { Count: > 0 })
        {
            sb.Append("\nChoices: <code>").Append(HtmlEscape(string.Join(", ", p.Enum))).Append("</code>");
        }
        await _provider.SendHtmlAsync(chat, sb.ToString(), cancellationToken);
    }

    private async Task ContinueParameterCollectionAsync(long chatId, PendingTaskState state, string text, CancellationToken cancellationToken)
    {
        var chat = ChatId.FromTelegram(chatId);

        if (state.Remaining.Count == 0)
        {
            // Defensive: shouldn't happen, but if it does, just clear and ignore.
            _conversation.Clear(chat);
            return;
        }

        var current = state.Remaining.Peek();
        if (!ParameterValueParser.TryParse(current, text, out var value, out var error))
        {
            _conversation.Touch(chat);
            await _provider.SendHtmlAsync(chat,
                $"⚠️ {HtmlEscape(error ?? "Invalid value.")} Try again, or /cancel.",
                cancellationToken);
            return;
        }

        state.Remaining.Dequeue();
        state.Collected[current.Name] = value;
        _conversation.Touch(chat);

        if (state.Remaining.Count > 0)
        {
            await PromptNextParameterAsync(chatId, state, cancellationToken);
            return;
        }

        // All required params collected — execute and clear.
        var task = state.Task;
        var collected = new Dictionary<string, object?>(state.Collected, StringComparer.OrdinalIgnoreCase);
        _conversation.Clear(chat);

        await _provider.SendHtmlAsync(chat,
            $"→ Running <code>{HtmlEscape(task.Name)}</code>{HtmlEscape(FormatParameterList(collected))}",
            cancellationToken);

        var result = await _executor.ExecuteAsync(task, collected, cancellationToken);
        await _dispatcher.DispatchAsync(_provider, ChatId.FromTelegram(chatId), result, cancellationToken);
    }

    private bool IsAuthorized(long userId, long chatId)
    {
        var hasAllowList = _options.AllowedUserIds.Length > 0 || _options.AllowedChatIds.Length > 0;
        if (!hasAllowList)
        {
            _logger.LogWarning("No Telegram allow-list configured; rejecting message from {UserId}.", userId);
            return false;
        }
        return _options.AllowedUserIds.Contains(userId)
            || _options.AllowedChatIds.Contains(chatId);
    }

    private static string FormatParameterList(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.Count == 0) return string.Empty;
        var pairs = parameters.Select(kv => $"{kv.Key}={kv.Value}");
        return $" ({string.Join(", ", pairs)})";
    }

    private static string RenderDryRun(TaskDefinition task, IReadOnlyDictionary<string, object?> parameters)
    {
        var sb = new StringBuilder();
        sb.Append("<b>Dry run</b>: <code>").Append(HtmlEscape(task.Name)).AppendLine("</code>");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.Append("<i>").Append(HtmlEscape(task.Description)).AppendLine("</i>");
        }
        sb.AppendLine();

        if (parameters.Count > 0)
        {
            sb.AppendLine("Parameters:");
            foreach (var (k, v) in parameters)
            {
                sb.Append("  • ").Append(HtmlEscape(k))
                  .Append(" = <code>").Append(HtmlEscape(v?.ToString() ?? "null")).AppendLine("</code>");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(task.Command))
        {
            var cmd = ParameterTemplate.Apply(task.Command, parameters);
            var args = ParameterTemplate.ApplyAll(task.Args, parameters);
            var line = new StringBuilder(cmd);
            foreach (var a in args) line.Append(' ').Append(a);
            sb.AppendLine("Would run:");
            sb.Append("<pre>").Append(HtmlEscape(line.ToString())).AppendLine("</pre>");
        }
        else
        {
            sb.AppendLine("(no command — output is collected directly)");
        }

        sb.Append("Output type: <code>").Append(task.Output.Type).Append("</code>");
        return sb.ToString();
    }

    private static string HtmlEscape(string input) =>
        input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task TrySendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _provider.SendTextAsync(ChatId.FromTelegram(chatId), text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send error message to Telegram");
        }
    }
}
