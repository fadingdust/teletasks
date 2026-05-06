using TeleTasks.Models;

namespace TeleTasks.Services.Chat;

/// <summary>
/// Renders a <see cref="TaskExecutionResult"/> to a chat by translating each
/// artifact into the matching <see cref="IChatProvider"/> send call.
/// Lives in <c>Services/Chat/</c> so the routing pipeline (host) and the
/// notifier (<c>JobNotifierService</c>, step 2c.2) can share the same
/// rendering logic without one importing the other.
///
/// Output shape:
/// <list type="bullet">
///   <item>No artifacts + failure → plain-text <see cref="TaskExecutionResult.ErrorMessage"/>.</item>
///   <item>Each <c>text</c> artifact → HTML with <c>&lt;pre&gt;</c>-wrapped body, optional <c>&lt;b&gt;</c> caption.</item>
///   <item>Each <c>image</c> artifact → <see cref="IChatProvider.SendImageAsync"/>.</item>
///   <item>Each <c>file</c>  artifact → <see cref="IChatProvider.SendDocumentAsync"/>.</item>
///   <item>Trailing failure with message → plain-text "⚠️ ..." line.</item>
/// </list>
/// HTML escape covers <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> only — the
/// minimal form Telegram's HTML parse mode requires; matches what the host
/// used inline before this extraction.
/// </summary>
public sealed class ChatResultDispatcher
{
    public async Task DispatchAsync(
        IChatProvider provider,
        ChatId chat,
        TaskExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (result.Artifacts.Count == 0 && !result.Success)
        {
            await provider.SendTextAsync(chat, result.ErrorMessage ?? "Task failed.", cancellationToken);
            return;
        }

        // A long-running job just started: the first text artifact is the
        // "Started job N..." status line. Render it with [Job N] [Stop N]
        // buttons so the user has a one-tap path to status / kill.
        OutputArtifact? jobStartArtifact = null;
        if (result.JobId is int jobId)
        {
            jobStartArtifact = result.Artifacts.FirstOrDefault(a => a.Kind == "text");
            var startText = jobStartArtifact?.Text ?? $"Started job {jobId}.";
            var keyboard = new[]
            {
                new[]
                {
                    new InlineButton($"Job {jobId}",  $"/job {jobId}"),
                    new InlineButton($"Stop {jobId}", $"/stop {jobId}")
                }
            };
            await provider.SendHtmlAsync(chat, $"<pre>{Escape(startText)}</pre>",
                keyboard, cancellationToken);
        }

        foreach (var artifact in result.Artifacts)
        {
            if (artifact == jobStartArtifact) continue;
            switch (artifact.Kind)
            {
                case "text":
                    var rawText = artifact.Text ?? string.Empty;
                    if (string.IsNullOrEmpty(rawText)) rawText = "(empty)";
                    var body = string.IsNullOrWhiteSpace(artifact.Caption)
                        ? $"<pre>{Escape(rawText)}</pre>"
                        : $"<b>{Escape(artifact.Caption)}</b>\n<pre>{Escape(rawText)}</pre>";
                    await provider.SendHtmlAsync(chat, body, cancellationToken);
                    break;
                case "image":
                    await provider.SendImageAsync(chat, artifact.Path!, artifact.Caption, cancellationToken);
                    break;
                case "file":
                    await provider.SendDocumentAsync(chat, artifact.Path!, artifact.Caption, cancellationToken);
                    break;
            }
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            await provider.SendTextAsync(chat, $"⚠️ {result.ErrorMessage}", cancellationToken);
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
