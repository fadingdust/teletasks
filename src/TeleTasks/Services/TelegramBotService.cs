using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TeleTasks.Configuration;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class TelegramBotService : BackgroundService
{
    private readonly TelegramOptions _options;
    private readonly TaskRegistry _registry;
    private readonly TaskMatcher _matcher;
    private readonly TaskExecutor _executor;
    private readonly OllamaClient _ollama;
    private readonly ILogger<TelegramBotService> _logger;

    private TelegramBotClient? _bot;
    private string? _botUsername;

    public TelegramBotService(
        IOptions<TelegramOptions> options,
        TaskRegistry registry,
        TaskMatcher matcher,
        TaskExecutor executor,
        OllamaClient ollama,
        ILogger<TelegramBotService> logger)
    {
        _options = options.Value;
        _registry = registry;
        _matcher = matcher;
        _executor = executor;
        _ollama = ollama;
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
        _bot = new TelegramBotClient(_options.Token, cancellationToken: stoppingToken);

        var me = await _bot.GetMe(stoppingToken);
        _botUsername = me.Username;
        _logger.LogInformation("Telegram bot @{Username} started ({Tasks} task(s) loaded).",
            _botUsername, _registry.Tasks.Count);

        await _bot.DropPendingUpdates(stoppingToken);

        _bot.OnError += OnErrorAsync;
        _bot.OnMessage += OnMessageAsync;

        await CheckOllamaHealthAndNotifyAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
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

        long? recipient = null;
        if (_options.AllowedUserIds.Length > 0) recipient = _options.AllowedUserIds[0];
        else if (_options.AllowedChatIds.Length > 0) recipient = _options.AllowedChatIds[0];

        if (recipient is null)
        {
            _logger.LogWarning("Startup notification not sent: no AllowedUserIds or AllowedChatIds configured.");
            return;
        }

        try
        {
            await _bot!.SendMessage(recipient.Value, htmlBody,
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send startup notification to {Recipient}", recipient);
        }
    }

    private async Task OnMessageAsync(Message message, UpdateType updateType)
    {
        var bot = _bot!;
        var ct = CancellationToken.None;

        if (message.Text is not { } text) return;

        var userId = message.From?.Id ?? 0;
        var chatId = message.Chat.Id;
        var username = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        if (!IsAuthorized(userId, chatId))
        {
            _logger.LogWarning("Unauthorized message from {User} ({UserId}) chat {ChatId}", username, userId, chatId);
            await bot.SendMessage(chatId, "Not authorized.", cancellationToken: ct);
            return;
        }

        _logger.LogInformation("Message from {User} ({UserId}): {Text}", username, userId, text);

        try
        {
            var dryRun = false;
            var routedText = text;
            if (text.StartsWith("/dry", StringComparison.OrdinalIgnoreCase) &&
                (text.Length == 4 || char.IsWhiteSpace(text[4])))
            {
                dryRun = true;
                routedText = text.Length > 4 ? text[4..].TrimStart() : string.Empty;
                if (string.IsNullOrWhiteSpace(routedText))
                {
                    await bot.SendMessage(chatId, "Usage: /dry <natural-language request>", cancellationToken: ct);
                    return;
                }
            }
            else if (text.StartsWith('/'))
            {
                await HandleCommandAsync(chatId, text, ct);
                return;
            }

            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            var match = await _matcher.MatchAsync(routedText, ct);
            if (match is null || string.IsNullOrEmpty(match.TaskName))
            {
                var reason = match?.Reasoning;
                var reply = string.IsNullOrWhiteSpace(reason)
                    ? "I couldn't find a task that matches that. Try /tasks to see what I can do."
                    : $"No matching task: {reason}";
                await bot.SendMessage(chatId, reply, cancellationToken: ct);
                return;
            }

            if (match.TaskName == TaskMatcher.ShowTasksRoute)
            {
                await bot.SendMessage(chatId, BuildTaskList(), cancellationToken: ct);
                return;
            }
            if (match.TaskName == TaskMatcher.ShowHelpRoute)
            {
                await bot.SendMessage(chatId, BuildHelp(), cancellationToken: ct);
                return;
            }
            if (match.TaskName == TaskMatcher.ShowResultsRoute)
            {
                var requested = match.Parameters.TryGetValue("task_name", out var tn) ? tn?.ToString() : null;
                await SendResultsAsync(chatId, requested, ct);
                return;
            }

            var task = _registry.Find(match.TaskName)!;

            if (dryRun)
            {
                await bot.SendMessage(chatId, RenderDryRun(task, match.Parameters),
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }

            await bot.SendMessage(chatId,
                $"→ Running <code>{HtmlEscape(task.Name)}</code>{HtmlEscape(FormatParameterList(match.Parameters))}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            var result = await _executor.ExecuteAsync(task, match.Parameters, ct);
            await SendResultAsync(chatId, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
            await TrySendAsync(chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task HandleCommandAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var bot = _bot!;
        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        var at = head.IndexOf('@');
        if (at > 0)
        {
            if (!head[(at + 1)..].Equals(_botUsername, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            head = head[..at];
        }

        switch (head.ToLowerInvariant())
        {
            case "/start":
            case "/help":
                await bot.SendMessage(chatId, BuildHelp(), cancellationToken: cancellationToken);
                break;
            case "/tasks":
                await bot.SendMessage(chatId, BuildTaskList(), cancellationToken: cancellationToken);
                break;
            case "/reload":
                _registry.Load();
                await bot.SendMessage(chatId, $"Reloaded {_registry.Tasks.Count} task(s).", cancellationToken: cancellationToken);
                break;
            case "/whoami":
                await bot.SendMessage(chatId, $"chat={chatId}", cancellationToken: cancellationToken);
                break;
            case "/results":
                {
                    var arg = space < 0 ? null : text[(space + 1)..].Trim();
                    await SendResultsAsync(chatId, arg, cancellationToken);
                    break;
                }
            default:
                await bot.SendMessage(chatId, "Unknown command. Try /help.", cancellationToken: cancellationToken);
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
        var bot = _bot!;

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            await bot.SendMessage(chatId,
                "Usage: /results <task-name>. See /tasks for the list.",
                cancellationToken: cancellationToken);
            return;
        }

        var task = _registry.Find(requestedName);
        if (task is null)
        {
            var disabled = _registry.DisabledTasks.FirstOrDefault(t =>
                string.Equals(t.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            var hint = disabled is not null ? " (it's disabled — flip enabled:true to use)" : "";
            await bot.SendMessage(chatId,
                $"No task named <code>{HtmlEscape(requestedName)}</code>{hint}.",
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return;
        }

        if (task.Output.Type == TaskOutputType.Text)
        {
            await bot.SendMessage(chatId,
                $"<code>{HtmlEscape(task.Name)}</code> has Text output (its stdout). " +
                "There's no cached state on disk to show — run the task to see results.",
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return;
        }

        TaskExecutionResult result;
        try
        {
            result = await _executor.EvaluateOutputAsync(task, parameters: null, cancellationToken);
        }
        catch (Exception ex)
        {
            await bot.SendMessage(chatId,
                $"Could not read latest output for <code>{HtmlEscape(task.Name)}</code>: {HtmlEscape(ex.Message)}",
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return;
        }

        if (result.Artifacts.Count == 0)
        {
            await bot.SendMessage(chatId,
                $"No output yet for <code>{HtmlEscape(task.Name)}</code>.",
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return;
        }

        await SendResultAsync(chatId, result, cancellationToken);
    }

    private string BuildHelp() =>
        "TeleTasks - chat in natural language to run pre-defined tasks.\n\n" +
        "Commands:\n" +
        "  /tasks         - list configured (and disabled) tasks\n" +
        "  /reload        - reload tasks.json\n" +
        "  /dry <text>    - resolve a task and show what would run, without running it\n" +
        "  /results <task> - show the latest output of <task> without running it\n" +
        "  /whoami        - show your user/chat IDs\n" +
        "  /help          - this message\n\n" +
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

    private async Task SendResultAsync(long chatId, TaskExecutionResult result, CancellationToken cancellationToken)
    {
        var bot = _bot!;

        if (result.Artifacts.Count == 0 && !result.Success)
        {
            await bot.SendMessage(chatId, result.ErrorMessage ?? "Task failed.", cancellationToken: cancellationToken);
            return;
        }

        foreach (var artifact in result.Artifacts)
        {
            switch (artifact.Kind)
            {
                case "text":
                    var rawText = artifact.Text ?? string.Empty;
                    if (string.IsNullOrEmpty(rawText)) rawText = "(empty)";
                    var body = string.IsNullOrWhiteSpace(artifact.Caption)
                        ? $"<pre>{HtmlEscape(rawText)}</pre>"
                        : $"<b>{HtmlEscape(artifact.Caption)}</b>\n<pre>{HtmlEscape(rawText)}</pre>";
                    await bot.SendMessage(chatId, body, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    break;
                case "image":
                    {
                        await using var fs = File.OpenRead(artifact.Path!);
                        var input = new Telegram.Bot.Types.InputFileStream(fs, Path.GetFileName(artifact.Path!));
                        await bot.SendPhoto(chatId, input, caption: artifact.Caption, cancellationToken: cancellationToken);
                        break;
                    }
                case "file":
                    {
                        await using var fs = File.OpenRead(artifact.Path!);
                        var input = new Telegram.Bot.Types.InputFileStream(fs, Path.GetFileName(artifact.Path!));
                        await bot.SendDocument(chatId, input, caption: artifact.Caption, cancellationToken: cancellationToken);
                        break;
                    }
            }
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            await bot.SendMessage(chatId, $"⚠️ {result.ErrorMessage}", cancellationToken: cancellationToken);
        }
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

    private Task OnErrorAsync(Exception exception, HandleErrorSource source)
    {
        var description = exception switch
        {
            ApiRequestException api => $"Telegram API error [{api.ErrorCode}] from {source}: {api.Message}",
            _ => $"{source}: {exception}"
        };
        _logger.LogError("{Error}", description);
        return Task.CompletedTask;
    }

    private async Task TrySendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _bot!.SendMessage(chatId, text, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send error message to Telegram");
        }
    }
}
