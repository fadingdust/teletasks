using System.Net;

namespace TeleTasks.Services.Chat;

/// <summary>
/// Provider-agnostic HTML escape for the Telegram-style markup we use as
/// the canonical wire format on <see cref="IChatProvider.SendHtmlAsync"/>.
/// Lives outside any specific provider so the routing pipeline can
/// pre-escape user content (task names, paths, exception messages)
/// without knowing which backend will render it.
/// </summary>
public static class ChatHtml
{
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlEncode(text);
}
