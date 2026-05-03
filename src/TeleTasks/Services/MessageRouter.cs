using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeleTasks.Models;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

/// <summary>
/// Provider-agnostic message router. Owns all slash-command dispatch,
/// natural-language task matching, parameter collection, and job queries.
/// The host (<c>ChatHost</c>) wires <see cref="HandleAsync"/> to a provider's
/// <c>OnMessage</c> event; the router never touches provider lifecycle.
/// </summary>
public sealed class MessageRouter
{
    private readonly IChatProvider _provider;
    private readonly TaskRegistry _registry;
    private readonly TaskMatcher _matcher;
    private readonly TaskExecutor _executor;
    private readonly ChatResultDispatcher _dispatcher;
    private readonly JobTracker _jobs;
    private readonly ConversationStateTracker _conversation;
    private readonly ILogger<MessageRouter> _logger;

    public MessageRouter(
        IChatProvider provider,
        TaskRegistry registry,
        TaskMatcher matcher,
        TaskExecutor executor,
        ChatResultDispatcher dispatcher,
        JobTracker jobs,
        ConversationStateTracker conversation,
        ILogger<MessageRouter> logger)
    {
        _provider = provider;
        _registry = registry;
        _matcher = matcher;
        _executor = executor;
        _dispatcher = dispatcher;
        _jobs = jobs;
        _conversation = conversation;
        _logger = logger;
    }

    public async Task HandleAsync(IncomingMessage message)
    {
        var ct = CancellationToken.None;
        var text = message.Text;
        if (string.IsNullOrEmpty(text)) return;

        var chat = message.Chat;
        var username = message.Username;

        if (!_provider.IsAuthorized(message))
        {
            _logger.LogWarning("Unauthorized message from {User} ({UserId}) in {Chat}", username, message.UserId, chat);
            await _provider.SendTextAsync(chat, "Not authorized.", ct);
            return;
        }

        _logger.LogInformation("Message from {User} ({UserId}): {Text}", username, message.UserId, text);

        try
        {
            var pending = _conversation.Get(chat);
            var pendingIntent = _conversation.GetIntent(chat);
            var isCommand = SlashCommand.IsCommand(text);

            if (!isCommand && pending is not null)
            {
                await ContinueParameterCollectionAsync(chat, pending, text, ct);
                return;
            }
            if (!isCommand && pendingIntent is not null)
            {
                _conversation.ClearIntent(chat);
                await ResolvePendingIntentAsync(chat, pendingIntent, text, ct);
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
            if (isCommand && pendingIntent is not null)
            {
                _conversation.ClearIntent(chat);
                if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    await _provider.SendTextAsync(chat, "Cancelled.", ct);
                    return;
                }
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
                await HandleCommandAsync(chat, text, ct);
                return;
            }

            await _provider.SendTypingAsync(chat, ct);

            // Fast path: exact task name skips the LLM call entirely.
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

            if (match is null)
            {
                await _provider.SendTextAsync(chat,
                    "I couldn't find a task that matches that. Try /tasks to see what I can do.", ct);
                return;
            }

            switch (match.Intent)
            {
                case TaskIntent.Help:
                    if (match.TaskName == TaskMatcher.ShowTasksRoute)
                        await _provider.SendHtmlAsync(chat, BuildTaskList(), ct);
                    else
                        await _provider.SendTextAsync(chat, BuildHelp(), ct);
                    return;

                case TaskIntent.Show:
                {
                    var requested = match.Parameters.TryGetValue("task_name", out var tn)
                        ? tn?.ToString() : null;
                    // When the model gave us a real task name (not a virtual route) use it directly.
                    if (string.IsNullOrEmpty(requested) && !TaskMatcher.IsVirtualRoute(match.TaskName))
                        requested = match.TaskName;
                    if (string.IsNullOrWhiteSpace(requested))
                    {
                        await PromptForShowTaskAsync(chat, ct);
                        return;
                    }
                    await SendResultsAsync(chat, requested, ct);
                    return;
                }

                case TaskIntent.Status:
                    if (match.TaskName == TaskMatcher.CheckLatestJobRoute)
                        await SendLatestJobStatusAsync(chat, ct);
                    else
                        await SendJobsListAsync(chat, ct);
                    return;

                case TaskIntent.Stop:
                    await HandleIntentStopAsync(chat, match, ct);
                    return;

                case TaskIntent.Restart:
                    await HandleIntentRestartAsync(chat, match, ct);
                    return;

                case TaskIntent.Cancel:
                    _conversation.Clear(chat);
                    await _provider.SendTextAsync(chat, "Nothing pending to cancel.", ct);
                    return;
            }

            // TaskIntent.Run — fall through to task execution.
            if (string.IsNullOrEmpty(match.TaskName))
            {
                var reason = match.Reasoning;
                var reply = string.IsNullOrWhiteSpace(reason)
                    ? "I couldn't find a task that matches that. Try /tasks to see what I can do."
                    : $"No matching task: {reason}";
                await _provider.SendTextAsync(chat, reply, ct);
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
                    $"-> <code>{HtmlEscape(task.Name)}</code> needs {missingRequired.Count} more value(s). " +
                    "Send each one in turn, or /cancel to abort.",
                    ct);
                await PromptNextParameterAsync(chat, state, ct);
                return;
            }

            await _provider.SendHtmlAsync(chat,
                $"-> Running <code>{HtmlEscape(task.Name)}</code>{HtmlEscape(FormatParameterList(match.Parameters))}",
                ct);

            var result = await _executor.ExecuteAsync(task, match.Parameters, ct);
            if (result.JobId is int newJobId)
            {
                _jobs.AssignChat(newJobId, chat);
            }
            await _dispatcher.DispatchAsync(_provider, chat, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
            await TrySendAsync(chat, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleCommandAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
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
                await _provider.SendHtmlAsync(chat, BuildTaskList(), cancellationToken);
                break;
            case "/reload":
                _registry.Load();
                await _provider.SendTextAsync(chat, $"Reloaded {_registry.Tasks.Count} task(s).", cancellationToken);
                break;
            case "/whoami":
                await _provider.SendTextAsync(chat, $"chat={chat.Id}", cancellationToken);
                break;
            case "/results":
                {
                    var arg = space < 0 ? null : text[(space + 1)..].Trim();
                    await SendResultsAsync(chat, arg, cancellationToken);
                    break;
                }
            case "/jobs":
                await SendJobsListAsync(chat, cancellationToken);
                break;
            case "/job":
                await HandleJobCommandAsync(chat, text, cancellationToken);
                break;
            case "/stop":
                await HandleStopCommandAsync(chat, text, cancellationToken);
                break;
            case "/restart":
                await HandleRestartCommandAsync(chat, text, cancellationToken);
                break;
            case "/clearjobs":
                await HandleClearJobsCommandAsync(chat, text, cancellationToken);
                break;
            case "/cancel":
                // OnIncomingAsync entry path already cleared any pending state
                // when a slash command arrived; this branch just acknowledges.
                await _provider.SendTextAsync(chat, "Nothing pending.", cancellationToken);
                break;
            default:
                await _provider.SendTextAsync(chat, "Unknown command. Try /help.", cancellationToken);
                break;
        }
    }

    private async Task SendResultsAsync(ChatId chat, string? requestedName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            await PromptForShowTaskAsync(chat, cancellationToken);
            return;
        }

        var task = _registry.Find(requestedName);
        if (task is null)
        {
            var disabled = _registry.DisabledTasks.FirstOrDefault(t =>
                string.Equals(t.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            var hint = disabled is not null ? " (it's disabled - flip enabled:true to use)" : "";
            await _provider.SendHtmlAsync(chat,
                $"No task named <code>{HtmlEscape(requestedName)}</code>{hint}.",
                cancellationToken);
            return;
        }

        if (task.Output.Type == TaskOutputType.Text)
        {
            await _provider.SendHtmlAsync(chat,
                $"<code>{HtmlEscape(task.Name)}</code> has Text output (its stdout). " +
                "There's no cached state on disk to show - run the task to see results.",
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

        await _dispatcher.DispatchAsync(_provider, chat, result, cancellationToken);
    }

    private async Task HandleJobCommandAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var id))
        {
            await SendJobsListAsync(chat, cancellationToken);
            return;
        }
        await SendJobStatusAsync(chat, id, cancellationToken);
    }

    private async Task HandleStopCommandAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
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

    private async Task HandleRestartCommandAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var id))
        {
            await _provider.SendTextAsync(chat, "Usage: /restart <job-id>. See /jobs for IDs.", cancellationToken);
            return;
        }

        var old = _jobs.Get(id);
        if (old is null)
        {
            await _provider.SendTextAsync(chat, $"No job with id {id}.", cancellationToken);
            return;
        }
        if (!old.IsFinished)
        {
            await _provider.SendTextAsync(chat,
                $"Job {id} ({old.TaskName}) is still running. Stop it first with /stop {id}.",
                cancellationToken);
            return;
        }
        if (old.Task is null || string.IsNullOrWhiteSpace(old.Task.Command))
        {
            await _provider.SendTextAsync(chat,
                $"Job {id} has no stored task definition and cannot be restarted.",
                cancellationToken);
            return;
        }

        var newJob = _jobs.Restart(id);
        if (newJob is null)
        {
            await _provider.SendTextAsync(chat, $"Could not restart job {id}.", cancellationToken);
            return;
        }
        _jobs.AssignChat(newJob.Id, chat);
        await _provider.SendHtmlAsync(chat,
            $"-> Restarted job {id} as job {newJob.Id}: <code>{HtmlEscape(newJob.TaskName)}</code>",
            cancellationToken);
    }

    private async Task HandleClearJobsCommandAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
        var space = text.IndexOf(' ');
        var arg = space < 0 ? null : text[(space + 1)..].Trim();
        var forceAll = string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase);

        _jobs.Refresh();
        var removed = _jobs.Prune(forceAll);
        var kept = _jobs.List().Count(j => j.IsFinished);

        var msg = forceAll
            ? $"Cleared all {removed} finished job(s)."
            : removed == 0
                ? "No finished jobs to prune (all within retention floor)."
                : $"Cleared {removed} finished job(s), kept {kept} per retention floor.";
        await _provider.SendTextAsync(chat, msg, cancellationToken);
    }

    private async Task SendJobsListAsync(ChatId chat, CancellationToken cancellationToken)
    {
        _jobs.Refresh();
        var jobs = _jobs.List();
        if (jobs.Count == 0)
        {
            await _provider.SendHtmlAsync(chat,
                "No jobs yet. Tasks with <code>longRunning: true</code> show up here once started.",
                cancellationToken);
            return;
        }

        var sb = new StringBuilder();
        var active = jobs.Where(j => !j.IsFinished).ToList();
        var finished = jobs.Where(j => j.IsFinished).Take(10).ToList();

        var keyboard = new List<IReadOnlyList<InlineButton>>();

        if (active.Count > 0)
        {
            sb.AppendLine("<b>Active</b>:");
            foreach (var j in active)
            {
                sb.Append("- /job ").Append(j.Id).Append(" - <code>")
                  .Append(HtmlEscape(j.TaskName)).Append("</code> running ")
                  .Append(HtmlEscape(FormatElapsed(j.Elapsed))).AppendLine();
                keyboard.Add(new InlineButton[]
                {
                    new($"Job {j.Id}", $"/job {j.Id}"),
                    new($"Stop {j.Id}", $"/stop {j.Id}")
                });
            }
        }
        if (finished.Count > 0)
        {
            if (active.Count > 0) sb.AppendLine();
            sb.AppendLine("<b>Recent</b>:");
            foreach (var j in finished)
            {
                var exit = FormatJobExit(j);
                sb.Append("- /job ").Append(j.Id).Append(" - <code>")
                  .Append(HtmlEscape(j.TaskName)).Append("</code> ")
                  .Append(HtmlEscape(exit)).Append(" after ")
                  .Append(HtmlEscape(FormatElapsed(j.Elapsed))).AppendLine();
                keyboard.Add(new InlineButton[]
                {
                    new($"Job {j.Id}", $"/job {j.Id}"),
                    new($"Restart {j.Id}", $"/restart {j.Id}")
                });
            }
        }

        await _provider.SendHtmlAsync(chat, sb.ToString(),
            keyboard.Count > 0 ? keyboard : null, cancellationToken);
    }

    private async Task SendLatestJobStatusAsync(ChatId chat, CancellationToken cancellationToken)
    {
        _jobs.Refresh();
        var latest = _jobs.List(50)
            .OrderByDescending(j => !j.IsFinished)
            .ThenByDescending(j => j.StartedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            await _provider.SendTextAsync(chat, "No jobs yet.", cancellationToken);
            return;
        }
        await SendJobStatusAsync(chat, latest.Id, cancellationToken);
    }

    private async Task SendJobStatusAsync(ChatId chat, int id, CancellationToken cancellationToken)
    {
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

        IReadOnlyList<IReadOnlyList<InlineButton>>? headerButtons = job.IsFinished
            ? (job.Task is { Command: { Length: > 0 } }
                ? new[] { new InlineButton[] { new($"Restart {id}", $"/restart {id}") } }
                : null)
            : new[] { new InlineButton[] { new($"Stop {id}", $"/stop {id}") } };

        await _provider.SendHtmlAsync(chat, header.ToString(), headerButtons, cancellationToken);

        var tail = _jobs.TailLog(id, 30);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            await _provider.SendHtmlAsync(chat,
                $"<b>Log tail</b>\n<pre>{HtmlEscape(tail)}</pre>",
                cancellationToken);
        }
        else if (File.Exists(job.LogPath) && !job.IsFinished)
        {
            await _provider.SendHtmlAsync(chat,
                "Log is empty so far. If this is a Python script, stdout is likely block-buffered " +
                "when redirected. Re-run with <code>PYTHONUNBUFFERED=1</code> in the task's env, " +
                "or invoke <code>python -u</code> / <code>stdbuf -oL python ...</code>.",
                cancellationToken);
        }

        if (job.Task is not null && job.Task.Output is { } outputSpec &&
            outputSpec.Type is not TaskOutputType.Text)
        {
            try
            {
                var result = await _executor.EvaluateOutputAsync(job.Task, job.Parameters, cancellationToken);
                var fresh = FilterArtifactsSince(result.Artifacts, job.StartedAtUtc);
                if (fresh.Count > 0)
                {
                    var freshResult = new TaskExecutionResult { Success = result.Success, ExitCode = result.ExitCode };
                    foreach (var a in fresh) freshResult.Artifacts.Add(a);
                    await _dispatcher.DispatchAsync(_provider, chat, freshResult, cancellationToken);
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
                // Path no longer accessible.
            }
        }
        return fresh;
    }

    private async Task PromptNextParameterAsync(ChatId chat, PendingTaskState state, CancellationToken cancellationToken)
    {
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

    private async Task ContinueParameterCollectionAsync(ChatId chat, PendingTaskState state, string text, CancellationToken cancellationToken)
    {
        if (state.Remaining.Count == 0)
        {
            _conversation.Clear(chat);
            return;
        }

        var current = state.Remaining.Peek();
        if (!ParameterValueParser.TryParse(current, text, out var value, out var error))
        {
            _conversation.Touch(chat);
            await _provider.SendHtmlAsync(chat,
                $"Warning: {HtmlEscape(error ?? "Invalid value.")} Try again, or /cancel.",
                cancellationToken);
            return;
        }

        state.Remaining.Dequeue();
        state.Collected[current.Name] = value;
        _conversation.Touch(chat);

        if (state.Remaining.Count > 0)
        {
            await PromptNextParameterAsync(chat, state, cancellationToken);
            return;
        }

        // All required params collected - execute and clear.
        var task = state.Task;
        var collected = new Dictionary<string, object?>(state.Collected, StringComparer.OrdinalIgnoreCase);
        _conversation.Clear(chat);

        await _provider.SendHtmlAsync(chat,
            $"-> Running <code>{HtmlEscape(task.Name)}</code>{HtmlEscape(FormatParameterList(collected))}",
            cancellationToken);

        var result = await _executor.ExecuteAsync(task, collected, cancellationToken);
        await _dispatcher.DispatchAsync(_provider, chat, result, cancellationToken);
    }

    private async Task PromptForShowTaskAsync(ChatId chat, CancellationToken cancellationToken)
    {
        _conversation.BeginIntent(chat, TaskIntent.Show);
        var sb = new StringBuilder();
        sb.Append("Which task's results would you like to see? Send the task name, or /cancel.");
        if (_registry.Tasks.Count > 0)
        {
            var sample = string.Join(", ", _registry.Tasks.Take(5).Select(t => $"<code>{HtmlEscape(t.Name)}</code>"));
            sb.Append("\nSee /tasks for the full list. Examples: ").Append(sample).Append('.');
        }
        await _provider.SendHtmlAsync(chat, sb.ToString(), cancellationToken);
    }

    private async Task ResolvePendingIntentAsync(ChatId chat, PendingIntentState state, string answer,
        CancellationToken cancellationToken)
    {
        var trimmed = answer.Trim();
        switch (state.Intent)
        {
            case TaskIntent.Show:
                await SendResultsAsync(chat, trimmed, cancellationToken);
                return;
            default:
                await _provider.SendTextAsync(chat,
                    "Lost track of what I was asking. Try again.", cancellationToken);
                return;
        }
    }

    private async Task HandleIntentStopAsync(ChatId chat, TaskMatch match, CancellationToken cancellationToken)
    {
        _jobs.Refresh();
        var active = _jobs.List().Where(j => !j.IsFinished).ToList();
        if (!string.IsNullOrEmpty(match.TaskName) && !TaskMatcher.IsVirtualRoute(match.TaskName))
            active = active.Where(j => j.TaskName == match.TaskName).ToList();

        if (active.Count == 0)
        {
            await _provider.SendTextAsync(chat, "No active jobs to stop.", cancellationToken);
            return;
        }
        if (active.Count > 1)
        {
            var ids = string.Join(", ", active.Select(j => $"/stop {j.Id}"));
            await _provider.SendTextAsync(chat,
                $"Multiple active jobs — use a specific command: {ids}", cancellationToken);
            return;
        }

        var job = active[0];
        var stopped = _jobs.Stop(job.Id);
        await _provider.SendTextAsync(chat,
            stopped
                ? $"Sent kill to job {job.Id} ({job.TaskName}, pid {job.Pid})."
                : $"Could not stop job {job.Id}. See logs.",
            cancellationToken);
    }

    private async Task HandleIntentRestartAsync(ChatId chat, TaskMatch match, CancellationToken cancellationToken)
    {
        _jobs.Refresh();
        var finished = _jobs.List()
            .Where(j => j.IsFinished)
            .OrderByDescending(j => j.StartedAtUtc)
            .ToList();
        if (!string.IsNullOrEmpty(match.TaskName) && !TaskMatcher.IsVirtualRoute(match.TaskName))
            finished = finished.Where(j => j.TaskName == match.TaskName).ToList();

        var latest = finished.FirstOrDefault();
        if (latest is null)
        {
            await _provider.SendTextAsync(chat, "No finished jobs to restart.", cancellationToken);
            return;
        }
        if (latest.Task is null || string.IsNullOrWhiteSpace(latest.Task.Command))
        {
            await _provider.SendTextAsync(chat,
                $"Job {latest.Id} has no stored task definition and cannot be restarted.",
                cancellationToken);
            return;
        }

        var newJob = _jobs.Restart(latest.Id);
        if (newJob is null)
        {
            await _provider.SendTextAsync(chat, $"Could not restart job {latest.Id}.", cancellationToken);
            return;
        }
        _jobs.AssignChat(newJob.Id, chat);
        await _provider.SendHtmlAsync(chat,
            $"-> Restarted job {latest.Id} as job {newJob.Id}: <code>{HtmlEscape(newJob.TaskName)}</code>",
            cancellationToken);
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
                sb.Append("  - ").Append(HtmlEscape(k))
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
            sb.AppendLine("(no command - output is collected directly)");
        }

        sb.Append("Output type: <code>").Append(task.Output.Type).Append("</code>");
        return sb.ToString();
    }

    private static string BuildHelp() =>
        "TeleTasks - chat in natural language to run pre-defined tasks.\n\n" +
        "Commands:\n" +
        "  /tasks          - list configured (and disabled) tasks\n" +
        "  /reload         - reload tasks.json\n" +
        "  /dry <text>     - resolve a task and show what would run, without running it\n" +
        "  /results <task> - show the latest output of <task> without running it\n" +
        "  /jobs           - list active and recent long-running jobs\n" +
        "  /job N          - status, log tail, and current output for job N\n" +
        "  /stop N         - kill a running job\n" +
        "  /restart N      - re-run a finished job with the same parameters\n" +
        "  /clearjobs      - prune finished jobs per retention policy\n" +
        "  /clearjobs all  - wipe all finished jobs (running jobs always kept)\n" +
        "  /cancel         - abort a pending parameter-collection prompt\n" +
        "  /whoami         - show your user/chat IDs\n" +
        "  /help           - this message\n\n" +
        "If a task needs values you didn't supply, I'll ask for them one at a time.\n" +
        "Anything else is sent to the local LLM for matching.";

    /// <summary>
    /// Renders the /tasks list as Telegram HTML. Task names are wrapped in
    /// <c>&lt;code&gt;</c> for monospace; backtick-quoted spans inside
    /// descriptions (e.g. "Run `run.sh`") are converted to <c>&lt;code&gt;</c>
    /// after HTML-escaping the rest of the description text.
    /// </summary>
    private string BuildTaskList()
    {
        if (_registry.Tasks.Count == 0 && _registry.DisabledTasks.Count == 0)
            return "No tasks configured.";

        var sb = new StringBuilder();
        sb.AppendLine("<b>Available tasks</b>:");
        foreach (var t in _registry.Tasks)
        {
            sb.Append("- <code>").Append(HtmlEscape(t.Name)).Append("</code>");
            sb.Append(" [").Append(string.Join(" · ", IntentsFor(t))).Append(']');
            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                sb.Append(" - ").Append(FormatDescription(t.Description));
            }
            sb.AppendLine();
        }

        if (_registry.DisabledTasks.Count > 0)
        {
            sb.AppendLine();
            sb.Append("<b>Disabled</b> (").Append(_registry.DisabledTasks.Count).AppendLine("):");
            foreach (var t in _registry.DisabledTasks)
            {
                sb.Append("- <code>").Append(HtmlEscape(t.Name)).AppendLine("</code>");
            }
        }
        return sb.ToString();
    }

    private static readonly Regex BacktickSpan = new(@"`([^`]+)`", RegexOptions.Compiled);

    /// <summary>
    /// HTML-escapes the description, then converts backtick-quoted spans
    /// (Markdown-style inline code) to <c>&lt;code&gt;</c>. Order matters —
    /// escape first, otherwise <c>&lt;</c>/<c>&gt;</c> inside backticks would
    /// be passed through as live HTML.
    /// </summary>
    internal static string FormatDescription(string description) =>
        BacktickSpan.Replace(HtmlEscape(description), "<code>$1</code>");

    /// <summary>
    /// Per-task intents inferred from the task's shape:
    ///   Run       — always (any enabled task can be executed)
    ///   Show      — when the task produces a non-Text artifact (file / images / log tail)
    ///   Status    — long-running only (only those become tracked jobs)
    ///   Stop      — long-running only (you can only kill what's tracked)
    ///   Restart   — long-running only (JobTracker only persists long-running runs)
    /// Cancel and Help are not task-scoped, so they never appear here.
    /// </summary>
    internal static IReadOnlyList<TaskIntent> IntentsFor(TaskDefinition task)
    {
        var intents = new List<TaskIntent> { TaskIntent.Run };
        if (task.Output.Type != TaskOutputType.Text) intents.Add(TaskIntent.Show);
        if (task.IsLongRunning)
        {
            intents.Add(TaskIntent.Status);
            intents.Add(TaskIntent.Stop);
            intents.Add(TaskIntent.Restart);
        }
        return intents;
    }

    internal static string HtmlEscape(string input) =>
        input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task TrySendAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _provider.SendTextAsync(chat, text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send error message");
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
}
