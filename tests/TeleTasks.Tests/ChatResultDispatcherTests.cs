using TeleTasks.Models;
using TeleTasks.Services.Chat;
using Xunit;

namespace TeleTasks.Tests;

public sealed class ChatResultDispatcherTests
{
    private readonly FakeChatProvider _chat = new();
    private readonly ChatResultDispatcher _dispatcher = new();
    private static readonly ChatId TestChat = ChatId.FromTelegram(42);

    [Fact]
    public async Task Long_running_start_attaches_job_and_stop_buttons()
    {
        var result = new TaskExecutionResult { Success = true, ExitCode = 0, JobId = 7 };
        result.Artifacts.Add(new OutputArtifact("text", null, null,
            "Started job 7 (render, pid 12345).\nLog: /tmp/render-7.log"));

        await _dispatcher.DispatchAsync(_chat, TestChat, result, CancellationToken.None);

        Assert.Single(_chat.SentHtmlsWithKeyboard);
        var msg = _chat.SentHtmlsWithKeyboard[0];
        Assert.Contains("Started job 7", msg.Html);

        var keyboard = msg.Keyboard;
        Assert.NotNull(keyboard);
        Assert.Single(keyboard);
        Assert.Equal(2, keyboard[0].Count);
        Assert.Equal("Job 7",   keyboard[0][0].Label);
        Assert.Equal("/job 7",  keyboard[0][0].CallbackData);
        Assert.Equal("Stop 7",  keyboard[0][1].Label);
        Assert.Equal("/stop 7", keyboard[0][1].CallbackData);
    }

    [Fact]
    public async Task Long_running_start_synthesizes_text_when_artifact_missing()
    {
        // Defensive — if a future code path forgets to add the text artifact,
        // the buttons should still go out so the user has a way to act.
        var result = new TaskExecutionResult { Success = true, JobId = 99 };

        await _dispatcher.DispatchAsync(_chat, TestChat, result, CancellationToken.None);

        Assert.Single(_chat.SentHtmlsWithKeyboard);
        Assert.Contains("Started job 99", _chat.SentHtmlsWithKeyboard[0].Html);
        Assert.NotNull(_chat.SentHtmlsWithKeyboard[0].Keyboard);
    }

    [Fact]
    public async Task Plain_text_artifact_without_JobId_uses_no_keyboard()
    {
        var result = new TaskExecutionResult { Success = true };
        result.Artifacts.Add(new OutputArtifact("text", null, null, "hello"));

        await _dispatcher.DispatchAsync(_chat, TestChat, result, CancellationToken.None);

        Assert.Single(_chat.SentHtmlsWithKeyboard);
        // Non-job rendering uses the no-keyboard overload — FakeChatProvider
        // records that with Keyboard == null.
        Assert.Null(_chat.SentHtmlsWithKeyboard[0].Keyboard);
        Assert.Contains("hello", _chat.SentHtmlsWithKeyboard[0].Html);
    }

    [Fact]
    public async Task Failure_with_no_artifacts_sends_error_text()
    {
        var result = new TaskExecutionResult { Success = false, ErrorMessage = "boom" };

        await _dispatcher.DispatchAsync(_chat, TestChat, result, CancellationToken.None);

        Assert.Single(_chat.SentTexts);
        Assert.Equal("boom", _chat.SentTexts[0].Text);
    }
}
