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

    /// <summary>
    /// For Images / Image: alongside the picked image(s), derive each one's
    /// Telegram caption from a sidecar file with the same basename
    /// (e.g. <c>0007.png</c> + <c>0007.json</c>).
    /// </summary>
    [JsonPropertyName("captionFrom")]
    public CaptionFromSpec? CaptionFrom { get; set; }

    /// <summary>
    /// For Images / Image: for each picked image, also send siblings with the
    /// same basename and these extensions as separate Telegram documents
    /// (e.g. <c>[".json", ".txt"]</c>).
    /// </summary>
    [JsonPropertyName("siblings")]
    public List<string>? Siblings { get; set; }
}

public sealed class CaptionFromSpec
{
    /// <summary>Sidecar file extension to look for, e.g. <c>".json"</c>.</summary>
    [JsonPropertyName("sidecar")]
    public string? Sidecar { get; set; }

    /// <summary>
    /// Optional caption template. Top-level scalar fields from the sidecar JSON
    /// can be substituted with <c>{key}</c>. If null, the caption is auto-built
    /// from the sidecar's keys (or just the variable keys when mode=auto-diff).
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>
    /// <c>"verbatim"</c> = every key shows in every caption.
    /// <c>"template"</c> = only the template's referenced keys, all images.
    /// <c>"auto-diff"</c> (default) = compute fields that are equal across the
    /// batch into a single header message; per-image captions show only the
    /// fields that vary.
    /// </summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; set; }
}
