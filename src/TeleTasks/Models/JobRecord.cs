using System.Text.Json.Serialization;
using TeleTasks.Services.Chat;

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
    /// Provider-qualified chat that started this job. Used by the notifier
    /// loop to push new artifacts and the completion summary back to the
    /// same conversation. Null for jobs started before push-notifications
    /// existed; those still work via /job N polling but won't get
    /// progressive pings. The associated <see cref="ChatIdJsonConverter"/>
    /// reads both the new <c>"telegram:42"</c> string form and the legacy
    /// bare <c>long</c> form, so existing <c>jobs.json</c> files survive
    /// the multi-provider upgrade.
    /// </summary>
    [JsonPropertyName("chatId")]
    public ChatId? ChatId { get; set; }

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

    [JsonPropertyName("restartedFrom")]
    public int? RestartedFromJobId { get; set; }

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
