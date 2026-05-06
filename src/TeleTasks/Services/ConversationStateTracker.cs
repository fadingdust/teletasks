using System.Collections.Concurrent;
using TeleTasks.Models;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

/// <summary>
/// Per-chat state for "I asked the user for the next required parameter, waiting
/// for their next message to be the value." Stored only in memory — losing it
/// across restarts is fine; the user re-prompts naturally.
///
/// State self-expires after <see cref="MaxIdle"/> so a user who walks away
/// mid-collection isn't trapped forever; the next message starts fresh.
///
/// Keyed by <see cref="ChatId"/> so a Telegram chat and a (future) Discord
/// channel with the same numeric id stay independent.
/// </summary>
public sealed class ConversationStateTracker
{
    public static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<ChatId, PendingTaskState> _states = new();
    private readonly ConcurrentDictionary<ChatId, PendingIntentState> _intents = new();

    public PendingTaskState? Get(ChatId chatId)
    {
        if (!_states.TryGetValue(chatId, out var state)) return null;
        if (DateTime.UtcNow - state.LastTouchedUtc > MaxIdle)
        {
            _states.TryRemove(chatId, out _);
            return null;
        }
        return state;
    }

    public PendingTaskState Begin(ChatId chatId, TaskDefinition task,
        IReadOnlyDictionary<string, object?> alreadyCollected,
        IEnumerable<TaskParameter> missingRequired)
    {
        // A new task collection supersedes any pending intent followup.
        _intents.TryRemove(chatId, out _);

        var state = new PendingTaskState
        {
            Task = task,
            StartedAtUtc = DateTime.UtcNow,
            LastTouchedUtc = DateTime.UtcNow
        };
        foreach (var (k, v) in alreadyCollected) state.Collected[k] = v;
        foreach (var p in missingRequired) state.Remaining.Enqueue(p);
        _states[chatId] = state;
        return state;
    }

    public void Touch(ChatId chatId)
    {
        if (_states.TryGetValue(chatId, out var state))
        {
            state.LastTouchedUtc = DateTime.UtcNow;
        }
    }

    public bool Clear(ChatId chatId) => _states.TryRemove(chatId, out _);

    /// <summary>
    /// Pending follow-up for an intent that needs one more piece of information
    /// (e.g. <c>Show</c> with no task name → "which task's results?"). The next
    /// non-command message becomes the answer; the router resolves it via
    /// intent-specific logic.
    /// </summary>
    public PendingIntentState? GetIntent(ChatId chatId)
    {
        if (!_intents.TryGetValue(chatId, out var state)) return null;
        if (DateTime.UtcNow - state.UpdatedUtc > MaxIdle)
        {
            _intents.TryRemove(chatId, out _);
            return null;
        }
        return state;
    }

    public void BeginIntent(ChatId chatId, TaskIntent intent)
    {
        // A new intent followup supersedes any pending task collection.
        _states.TryRemove(chatId, out _);
        _intents[chatId] = new PendingIntentState
        {
            Intent = intent,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    public bool ClearIntent(ChatId chatId) => _intents.TryRemove(chatId, out _);
}

public sealed class PendingTaskState
{
    public required TaskDefinition Task { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime LastTouchedUtc { get; set; }
    public Dictionary<string, object?> Collected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<TaskParameter> Remaining { get; } = new();
}

public sealed class PendingIntentState
{
    public required TaskIntent Intent { get; init; }
    public DateTime UpdatedUtc { get; set; }
}
