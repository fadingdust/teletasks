using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeleTasks.Models;

public enum TaskOutputType
{
    Text,
    File,
    Image,
    Images,
    LogTail
}

public sealed class TaskOutputSpec
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskOutputType Type { get; set; } = TaskOutputType.Text;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("directory")]
    public string? Directory { get; set; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("count")]
    public JsonElement? Count { get; set; }

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "newest";

    [JsonPropertyName("lines")]
    public JsonElement? Lines { get; set; }

    [JsonPropertyName("maxLength")]
    public int MaxLength { get; set; } = 3500;

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("includeStderr")]
    public bool IncludeStderr { get; set; } = true;
}
