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

    [JsonPropertyName("parameters")]
    public List<TaskParameter> Parameters { get; set; } = new();

    [JsonPropertyName("output")]
    public TaskOutputSpec Output { get; set; } = new();
}
