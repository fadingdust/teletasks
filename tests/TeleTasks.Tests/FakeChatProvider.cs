using TeleTasks.Services.Chat;

namespace TeleTasks.Tests;

/// <summary>
/// In-memory <see cref="IChatProvider"/> for router unit tests.
/// Every send call appends to the corresponding list; use
/// <see cref="RaiseAsync"/> to deliver an inbound message.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    public string Name => "fake";
    public ChatId? DefaultRecipient { get; set; }
    public bool IsReady => true;

    public event Func<IncomingMessage, Task>? OnMessage;

    private bool _authorizeAll = true;
    private HashSet<string>? _allowedUserIds;

    public List<(ChatId Chat, string Text)> SentTexts { get; } = new();
    public List<(ChatId Chat, string Html)> SentHtmls { get; } = new();
    public List<(ChatId Chat, string Path, string? Caption)> SentImages { get; } = new();
    public List<(ChatId Chat, string Path, string? Caption)> SentDocuments { get; } = new();
    public List<ChatId> TypingIndicators { get; } = new();

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendTextAsync(ChatId chat, string text, CancellationToken ct)
    {
        SentTexts.Add((chat, text));
        return Task.CompletedTask;
    }

    public Task SendHtmlAsync(ChatId chat, string html, CancellationToken ct)
    {
        SentHtmls.Add((chat, html));
        return Task.CompletedTask;
    }

    public Task SendImageAsync(ChatId chat, string path, string? caption, CancellationToken ct)
    {
        SentImages.Add((chat, path, caption));
        return Task.CompletedTask;
    }

    public Task SendDocumentAsync(ChatId chat, string path, string? caption, CancellationToken ct)
    {
        SentDocuments.Add((chat, path, caption));
        return Task.CompletedTask;
    }

    public Task SendTypingAsync(ChatId chat, CancellationToken ct)
    {
        TypingIndicators.Add(chat);
        return Task.CompletedTask;
    }

    public bool IsAuthorized(IncomingMessage message)
    {
        if (_authorizeAll) return true;
        return _allowedUserIds?.Contains(message.UserId) ?? false;
    }

    public void DenyAll() { _authorizeAll = false; _allowedUserIds = null; }
    public void Allow(string userId) { _authorizeAll = false; (_allowedUserIds ??= new()).Add(userId); }

    public async Task RaiseAsync(IncomingMessage message)
    {
        if (OnMessage is { } handler)
            await handler(message);
    }

    public static IncomingMessage Msg(
        long chatId,
        string text,
        string userId = "1",
        string username = "testuser") =>
        new(ChatId.FromTelegram(chatId), userId, username, text);
}
