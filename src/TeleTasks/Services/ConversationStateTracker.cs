using System.Collections.Concurrent;
using TeleTasks.Models;

namespace TeleTasks.Services;

/// <summary>
/// Per-chat state for "I asked the user for the next required parameter, waiting
/// for their next message to be the value." Stored only in memory — losing it
/// across restarts is fine; the user re-prompts naturally.
///
/// State self-expires after <see cref="MaxIdle"/> so a user who walks away
/// mid-collection isn't trapped forever; the next message starts fresh.
/// </summary>
public sealed class ConversationStateTracker
{
    public static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<long, PendingTaskState> _states = new();

    public PendingTaskState? Get(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state)) return null;
        if (DateTime.UtcNow - state.LastTouchedUtc > MaxIdle)
        {
            _states.TryRemove(chatId, out _);
            return null;
        }
        return state;
    }

    public PendingTaskState Begin(long chatId, TaskDefinition task,
        IReadOnlyDictionary<string, object?> alreadyCollected,
        IEnumerable<TaskParameter> missingRequired)
    {
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

    public void Touch(long chatId)
    {
        if (_states.TryGetValue(chatId, out var state))
        {
            state.LastTouchedUtc = DateTime.UtcNow;
        }
    }

    public bool Clear(long chatId) => _states.TryRemove(chatId, out _);
}

public sealed class PendingTaskState
{
    public required TaskDefinition Task { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime LastTouchedUtc { get; set; }
    public Dictionary<string, object?> Collected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<TaskParameter> Remaining { get; } = new();
}
