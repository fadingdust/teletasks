using System.Text.Json.Serialization;

namespace TeleTasks.Models;

public sealed class TaskCatalog
{
    [JsonPropertyName("tasks")]
    public List<TaskDefinition> Tasks { get; set; } = new();
}

public sealed class TaskDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonIgnore]
    public bool IsEnabled => Enabled ?? true;

    /// <summary>
    /// Where this task originated. Set by discover commands (e.g. "Makefile:build",
    /// "git:teletasks:status", "log:/var/log/syslog"). Empty/null means the task
    /// is hand-managed and discover will not touch it on re-run.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new();

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// When true the task is spawned detached: stdout+stderr go to a log file
    /// in the run-logs dir, the executor returns a job id immediately, and
    /// <see cref="TimeoutSeconds"/> is ignored (the job owns its own lifecycle).
    /// Use /jobs, /job N, /stop N to manage running tasks.
    /// </summary>
    [JsonPropertyName("longRunning")]
    public bool? LongRunning { get; set; }

    [JsonIgnore]
    public bool IsLongRunning => LongRunning ?? false;

    [JsonPropertyName("parameters")]
    public List<TaskParameter> Parameters { get; set; } = new();

    [JsonPropertyName("output")]
    public TaskOutputSpec Output { get; set; } = new();
}
