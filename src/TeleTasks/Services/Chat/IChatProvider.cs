namespace TeleTasks.Services.Chat;

/// <summary>
/// Abstract chat-backend contract. The same routing pipeline consumes
/// messages from any provider that implements this; per-provider
/// differences (Telegram's HTML + long IDs, Discord's Markdown +
/// gateway WebSocket, Matrix's federation-aware room IDs, Slack's
/// OAuth + mrkdwn) live behind the implementation.
///
/// Providers are responsible for:
///   1. Connecting to their backend (long-poll, gateway WebSocket,
///      webhook listener — all hidden behind <see cref="StartAsync"/>).
///   2. Translating native messages to <see cref="IncomingMessage"/>
///      and raising <see cref="OnMessage"/>. This includes stripping
///      the bot's own mention prefix (<c>@MyBot</c> on Telegram,
///      <c>&lt;@123&gt;</c> on Discord) so the matcher gets the
///      user's actual sentence.
///   3. Implementing the send primitives below. <see cref="SendHtmlAsync"/>
///      uses Telegram-style HTML markup as the wire format
///      (<c>&lt;code&gt;</c>, <c>&lt;pre&gt;</c>, <c>&lt;b&gt;</c>,
///      <c>&lt;i&gt;</c>); Telegram passes it through, other providers
///      translate to their native flavour (Markdown, mrkdwn).
///   4. Implementing <see cref="IsAuthorized"/> against their per-provider
///      allow-list.
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable identifier used in <see cref="ChatId.Provider"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Optional "primary" chat to send unsolicited bot-initiated messages to
    /// (startup health warnings, etc.). Telegram returns the first
    /// AllowedUserId / AllowedChatId; Discord would return the first
    /// AllowedUserId. Null means "no obvious primary recipient, skip
    /// unsolicited DMs." Returns null until <see cref="StartAsync"/>
    /// completes.
    /// </summary>
    ChatId? DefaultRecipient { get; }

    /// <summary>True once <see cref="StartAsync"/> has connected and the provider is ready to send/receive.</summary>
    bool IsReady { get; }

    event Func<IncomingMessage, Task>? OnMessage;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Send a plain-text message. No formatting, no escaping needed by callers.</summary>
    Task SendTextAsync(ChatId chat, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Send a message using Telegram-style HTML markup as the canonical
    /// wire format. Supported tags: <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>,
    /// <c>&lt;code&gt;</c>, <c>&lt;pre&gt;</c>. Callers must pre-escape
    /// any literal &amp;/&lt;/&gt; in user-content via the
    /// provider-agnostic <see cref="ChatHtml.Escape"/>.
    /// </summary>
    Task SendHtmlAsync(ChatId chat, string html, CancellationToken cancellationToken);

    Task SendImageAsync(ChatId chat, string path, string? caption, CancellationToken cancellationToken);

    Task SendDocumentAsync(ChatId chat, string path, string? caption, CancellationToken cancellationToken);

    /// <summary>Best-effort "the bot is typing…" indicator. Optional.</summary>
    Task SendTypingAsync(ChatId chat, CancellationToken cancellationToken);

    /// <summary>
    /// Provider-specific allow-list check. Telegram uses user/chat IDs,
    /// Discord adds guild/channel IDs, Slack adds workspace IDs.
    /// </summary>
    bool IsAuthorized(IncomingMessage message);
}
