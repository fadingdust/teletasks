using System.Text.Json.Serialization;

namespace TeleTasks.Models;

public sealed class TaskParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("default")]
    public object? Default { get; set; }

    [JsonPropertyName("enum")]
    public List<string>? Enum { get; set; }
}
