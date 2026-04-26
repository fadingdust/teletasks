namespace TeleTasks.Services.Chat;

/// <summary>
/// Provider-agnostic shape of "the user just said something in a chat".
/// Each <see cref="IChatProvider"/> translates its native message type
/// into this so the routing / matching / conversation logic can be
/// shared.
/// </summary>
public sealed record IncomingMessage(
    ChatId Chat,
    string UserId,         // provider-native user id, stringified
    string Username,       // best-effort display name; falls back to "unknown"
    string Text);
