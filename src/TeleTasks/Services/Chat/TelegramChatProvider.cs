using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TeleTasks.Configuration;

namespace TeleTasks.Services.Chat;

/// <summary>
/// <see cref="IChatProvider"/> backed by Telegram.Bot. Owns the long-poll
/// connection, translates Telegram <see cref="Message"/> objects to
/// <see cref="IncomingMessage"/>, and sends responses using
/// Telegram-style HTML (which is the canonical wire format for
/// <see cref="IChatProvider.SendHtmlAsync"/>, so this provider just
/// passes through).
///
/// Wired up by the future <c>ChatHost</c> background service. The current
/// <c>TelegramBotService</c> still owns its own <c>TelegramBotClient</c>
/// directly — phase 2 of the multi-provider refactor (see IDEAS.md)
/// rewires it to delegate through this provider.
/// </summary>
public sealed class TelegramChatProvider : IChatProvider
{
    public const string ProviderName = "telegram";

    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramChatProvider> _logger;

    private TelegramBotClient? _bot;
    private string? _botUsername;

    public TelegramChatProvider(
        IOptions<TelegramOptions> options,
        ILogger<TelegramChatProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => ProviderName;

    public event Func<IncomingMessage, Task>? OnMessage;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogError("Telegram:Token not configured. Provider will not start.");
            return;
        }

        _bot = new TelegramBotClient(_options.Token, cancellationToken: cancellationToken);

        var me = await _bot.GetMe(cancellationToken);
        _botUsername = me.Username;
        _logger.LogInformation("Telegram bot @{Username} started.", _botUsername);

        await _bot.DropPendingUpdates(cancellationToken);

        _bot.OnError += OnTelegramError;
        _bot.OnMessage += OnTelegramMessage;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bot is null) return Task.CompletedTask;
        _bot.OnError -= OnTelegramError;
        _bot.OnMessage -= OnTelegramMessage;
        return Task.CompletedTask;
    }

    public async Task SendTextAsync(ChatId chat, string text, CancellationToken cancellationToken)
    {
        if (_bot is null) return;
        await _bot.SendMessage(ToLong(chat), text, cancellationToken: cancellationToken);
    }

    public async Task SendHtmlAsync(ChatId chat, string html, CancellationToken cancellationToken)
    {
        if (_bot is null) return;
        await _bot.SendMessage(ToLong(chat), html, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
    }

    public async Task SendImageAsync(ChatId chat, string path, string? caption, CancellationToken cancellationToken)
    {
        if (_bot is null) return;
        await using var fs = File.OpenRead(path);
        var input = new InputFileStream(fs, Path.GetFileName(path));
        await _bot.SendPhoto(ToLong(chat), input, caption: caption, cancellationToken: cancellationToken);
    }

    public async Task SendDocumentAsync(ChatId chat, string path, string? caption, CancellationToken cancellationToken)
    {
        if (_bot is null) return;
        await using var fs = File.OpenRead(path);
        var input = new InputFileStream(fs, Path.GetFileName(path));
        await _bot.SendDocument(ToLong(chat), input, caption: caption, cancellationToken: cancellationToken);
    }

    public async Task SendTypingAsync(ChatId chat, CancellationToken cancellationToken)
    {
        if (_bot is null) return;
        await _bot.SendChatAction(ToLong(chat), ChatAction.Typing, cancellationToken: cancellationToken);
    }

    public bool IsAuthorized(IncomingMessage message)
    {
        var hasAllowList = _options.AllowedUserIds.Length > 0 || _options.AllowedChatIds.Length > 0;
        if (!hasAllowList)
        {
            _logger.LogWarning("No Telegram allow-list configured; rejecting message from {UserId}.", message.UserId);
            return false;
        }
        if (long.TryParse(message.UserId, out var userId) && _options.AllowedUserIds.Contains(userId))
            return true;
        if (long.TryParse(message.Chat.Id, out var chatId) && _options.AllowedChatIds.Contains(chatId))
            return true;
        return false;
    }

    private async Task OnTelegramMessage(Message message, UpdateType updateType)
    {
        if (message.Text is not { } text) return;
        if (OnMessage is null) return;

        // Strip leading "@MyBot" mention so the routing pipeline sees the
        // user's actual sentence. Drop the message entirely when a slash
        // command is addressed to a different bot in the same group chat.
        if (TryStripBotMention(text, out var stripped, out var addressedToUs) && !addressedToUs)
        {
            return;
        }

        var inbound = new IncomingMessage(
            Chat: ChatId.FromTelegram(message.Chat.Id),
            UserId: (message.From?.Id ?? 0).ToString(),
            Username: message.From?.Username ?? message.From?.FirstName ?? "unknown",
            Text: stripped ?? text);

        await OnMessage.Invoke(inbound);
    }

    /// <summary>
    /// Recognises Telegram's <c>/cmd@botname</c> form. Returns true if
    /// a mention was present (whether or not it was for us); the
    /// out-param <paramref name="addressedToUs"/> distinguishes the two.
    /// When addressed to us, <paramref name="stripped"/> is the text
    /// with the <c>@botname</c> portion removed.
    /// </summary>
    private bool TryStripBotMention(string text, out string? stripped, out bool addressedToUs)
    {
        stripped = null;
        addressedToUs = true;
        if (string.IsNullOrEmpty(_botUsername)) return false;
        if (!text.StartsWith('/')) return false;
        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        var at = head.IndexOf('@');
        if (at <= 0) return false;
        var mention = head[(at + 1)..];
        addressedToUs = mention.Equals(_botUsername, StringComparison.OrdinalIgnoreCase);
        if (!addressedToUs) return true;
        stripped = head[..at] + (space < 0 ? string.Empty : text[space..]);
        return true;
    }

    private static long ToLong(ChatId chat)
    {
        if (long.TryParse(chat.Id, out var l)) return l;
        throw new InvalidOperationException(
            $"Telegram chat id '{chat.Id}' isn't a long — wrong provider for ChatId {chat}?");
    }

    private Task OnTelegramError(Exception exception, HandleErrorSource source)
    {
        var description = exception switch
        {
            ApiRequestException api => $"Telegram API error [{api.ErrorCode}] from {source}: {api.Message}",
            _ => $"{source}: {exception}"
        };
        _logger.LogError("{Error}", description);
        return Task.CompletedTask;
    }
}
