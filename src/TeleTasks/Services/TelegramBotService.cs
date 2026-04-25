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
    private readonly ILogger<TelegramBotService> _logger;

    private TelegramBotClient? _bot;
    private string? _botUsername;

    public TelegramBotService(
        IOptions<TelegramOptions> options,
        TaskRegistry registry,
        TaskMatcher matcher,
        TaskExecutor executor,
        ILogger<TelegramBotService> logger)
    {
        _options = options.Value;
        _registry = registry;
        _matcher = matcher;
        _executor = executor;
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

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
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
            if (text.StartsWith('/'))
            {
                await HandleCommandAsync(chatId, text, ct);
                return;
            }

            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            var match = await _matcher.MatchAsync(text, ct);
            if (match is null || string.IsNullOrEmpty(match.TaskName))
            {
                var reason = match?.Reasoning;
                var reply = string.IsNullOrWhiteSpace(reason)
                    ? "I couldn't find a task that matches that. Try /tasks to see what I can do."
                    : $"No matching task: {reason}";
                await bot.SendMessage(chatId, reply, cancellationToken: ct);
                return;
            }

            var task = _registry.Find(match.TaskName)!;
            await bot.SendMessage(chatId,
                $"→ Running `{task.Name}`{FormatParameterList(match.Parameters)}",
                parseMode: ParseMode.Markdown,
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
            default:
                await bot.SendMessage(chatId, "Unknown command. Try /help.", cancellationToken: cancellationToken);
                break;
        }
    }

    private string BuildHelp() =>
        "TeleTasks - chat in natural language to run pre-defined tasks.\n\n" +
        "Commands:\n" +
        "  /tasks  - list configured tasks\n" +
        "  /reload - reload tasks.json\n" +
        "  /whoami - show your user/chat IDs\n" +
        "  /help   - this message\n\n" +
        "Anything else is sent to the local LLM for matching.";

    private string BuildTaskList()
    {
        if (_registry.Tasks.Count == 0) return "No tasks configured.";
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
                    var body = string.IsNullOrWhiteSpace(artifact.Caption)
                        ? artifact.Text ?? string.Empty
                        : $"*{Escape(artifact.Caption)}*\n```\n{artifact.Text}\n```";
                    if (string.IsNullOrEmpty(body)) body = "(empty)";
                    await bot.SendMessage(chatId, body, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
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

    private static string Escape(string input) =>
        input.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`");

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
