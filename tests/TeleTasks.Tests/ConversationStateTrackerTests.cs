using TeleTasks.Models;
using TeleTasks.Services;
using TeleTasks.Services.Chat;
using Xunit;

namespace TeleTasks.Tests;

public class ConversationStateTrackerTests
{
    private static TaskDefinition Task(string name = "tail_log")
    {
        var t = new TaskDefinition { Name = name };
        t.Parameters.Add(new TaskParameter { Name = "path", Type = "string", Required = true });
        t.Parameters.Add(new TaskParameter { Name = "lines", Type = "integer", Required = true });
        return t;
    }

    // Tests originally used bare-long chat IDs; the tracker is now keyed by
    // provider-qualified ChatId. This helper preserves the assertion shape
    // without wrapping every literal in ChatId.FromTelegram(...).
    private static ChatId Chat(long id) => ChatId.FromTelegram(id);

    [Fact]
    public void Get_returns_null_for_unknown_chat()
    {
        var tracker = new ConversationStateTracker();
        Assert.Null(tracker.Get(Chat(999)));
    }

    [Fact]
    public void Begin_then_Get_returns_the_same_state()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        var state = tracker.Begin(chatId: Chat(1), task,
            alreadyCollected: new Dictionary<string, object?>(),
            missingRequired: task.Parameters);

        var fetched = tracker.Get(Chat(1));
        Assert.Same(state, fetched);
    }

    [Fact]
    public void Begin_seeds_collected_with_already_extracted_values()
    {
        var tracker = new ConversationStateTracker();
        var alreadyCollected = new Dictionary<string, object?> { ["lines"] = 50L };
        var task = Task();
        var missing = task.Parameters.Where(p => p.Name == "path").ToList();

        var state = tracker.Begin(Chat(1), task, alreadyCollected, missing);

        Assert.Equal(50L, state.Collected["lines"]);
        Assert.Single(state.Remaining);
        Assert.Equal("path", state.Remaining.Peek().Name);
    }

    [Fact]
    public void Begin_replaces_an_existing_state_for_the_same_chat()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        var first  = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);
        var second = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);

        Assert.NotSame(first, second);
        Assert.Same(second, tracker.Get(Chat(1)));
    }

    [Fact]
    public void Different_chats_keep_independent_state()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        var a = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);
        var b = tracker.Begin(Chat(2), task, new Dictionary<string, object?>(), task.Parameters);

        Assert.Same(a, tracker.Get(Chat(1)));
        Assert.Same(b, tracker.Get(Chat(2)));
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Clear_removes_state_and_returns_true()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);

        Assert.True(tracker.Clear(Chat(1)));
        Assert.Null(tracker.Get(Chat(1)));
    }

    [Fact]
    public void Clear_returns_false_for_unknown_chat()
    {
        var tracker = new ConversationStateTracker();
        Assert.False(tracker.Clear(Chat(404)));
    }

    [Fact]
    public void Touch_refreshes_LastTouchedUtc()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        var state = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);

        var earlier = DateTime.UtcNow.AddMinutes(-5);
        state.LastTouchedUtc = earlier;
        tracker.Touch(Chat(1));

        Assert.True(state.LastTouchedUtc > earlier);
    }

    [Fact]
    public void Touch_is_a_noop_for_unknown_chat()
    {
        // Should not throw.
        new ConversationStateTracker().Touch(Chat(999));
    }

    [Fact]
    public void Get_drops_state_that_has_been_idle_past_MaxIdle()
    {
        // We avoid a 15-minute Thread.Sleep by backdating LastTouchedUtc
        // beyond the threshold directly. Get is the GC trigger — it both
        // checks expiry and returns null if expired.
        var tracker = new ConversationStateTracker();
        var task = Task();
        var state = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);

        state.LastTouchedUtc = DateTime.UtcNow - ConversationStateTracker.MaxIdle - TimeSpan.FromSeconds(1);

        Assert.Null(tracker.Get(Chat(1)));
        // Subsequent Get also returns null (entry was removed, not just hidden).
        Assert.Null(tracker.Get(Chat(1)));
    }

    [Fact]
    public void Get_keeps_state_that_is_just_inside_MaxIdle()
    {
        var tracker = new ConversationStateTracker();
        var task = Task();
        var state = tracker.Begin(Chat(1), task, new Dictionary<string, object?>(), task.Parameters);

        // 10 seconds inside the window — should still be alive.
        state.LastTouchedUtc = DateTime.UtcNow - ConversationStateTracker.MaxIdle + TimeSpan.FromSeconds(10);

        Assert.Same(state, tracker.Get(Chat(1)));
    }
}
