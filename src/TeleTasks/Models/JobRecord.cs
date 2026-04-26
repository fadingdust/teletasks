using System.Text.Json.Serialization;

namespace TeleTasks.Models;

public sealed class JobRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("taskName")]
    public string TaskName { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = string.Empty;

    [JsonPropertyName("exitCodePath")]
    public string? ExitCodePath { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAtUtc { get; set; }

    [JsonPropertyName("finishedAt")]
    public DateTime? FinishedAtUtc { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("killed")]
    public bool Killed { get; set; }

    [JsonPropertyName("task")]
    public TaskDefinition? Task { get; set; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, object?> Parameters { get; set; } = new();

    /// <summary>
    /// Telegram chat that started this job. Used by the notifier loop to push
    /// new artifacts and the completion summary back to the same conversation.
    /// Null for jobs started before push-notifications existed; those still
    /// work via /job N polling but won't get progressive pings.
    /// </summary>
    [JsonPropertyName("chatId")]
    public long? ChatId { get; set; }

    /// <summary>
    /// Artifact paths the notifier loop has already pushed. Persisted so a bot
    /// restart doesn't re-send everything.
    /// </summary>
    [JsonPropertyName("seenArtifacts")]
    public List<string> SeenArtifactPaths { get; set; } = new();

    /// <summary>
    /// Set true after the completion summary lands once. Prevents duplicate
    /// "job N finished" pushes if the bot restarts after a job ended.
    /// </summary>
    [JsonPropertyName("completionNotified")]
    public bool CompletionNotified { get; set; }

    [JsonIgnore]
    public bool IsFinished => FinishedAtUtc.HasValue;

    [JsonIgnore]
    public TimeSpan Elapsed => (FinishedAtUtc ?? DateTime.UtcNow) - StartedAtUtc;
}

public sealed class JobRegistry
{
    [JsonPropertyName("nextId")]
    public int NextId { get; set; } = 1;

    [JsonPropertyName("jobs")]
    public List<JobRecord> Jobs { get; set; } = new();
}
