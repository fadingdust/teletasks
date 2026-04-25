using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class OutputCollector
{
    private readonly ILogger<OutputCollector> _logger;

    public OutputCollector(ILogger<OutputCollector> logger)
    {
        _logger = logger;
    }

    public async Task CollectAsync(
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> parameters,
        string stdout,
        string stderr,
        TaskExecutionResult result,
        CancellationToken cancellationToken)
    {
        var spec = task.Output;
        var caption = string.IsNullOrWhiteSpace(spec.Caption)
            ? null
            : ParameterTemplate.Apply(spec.Caption, parameters);

        switch (spec.Type)
        {
            case TaskOutputType.Text:
                {
                    var text = stdout;
                    if (spec.IncludeStderr && !string.IsNullOrWhiteSpace(stderr))
                    {
                        text = string.IsNullOrWhiteSpace(text) ? stderr : $"{text}\n--- stderr ---\n{stderr}";
                    }
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        text = "(no output)";
                    }
                    result.Artifacts.Add(new OutputArtifact("text", null, caption, Truncate(text, spec.MaxLength)));
                    break;
                }
            case TaskOutputType.File:
                {
                    var path = Resolve(spec.Path!, parameters);
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException($"Output file not found: {path}", path);
                    }
                    result.Artifacts.Add(new OutputArtifact("file", path, caption, null));
                    break;
                }
            case TaskOutputType.Image:
                {
                    var path = Resolve(spec.Path!, parameters);
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException($"Output image not found: {path}", path);
                    }
                    result.Artifacts.Add(new OutputArtifact("image", path, caption, null));
                    break;
                }
            case TaskOutputType.Images:
                {
                    var dir = Resolve(spec.Directory!, parameters);
                    if (!Directory.Exists(dir))
                    {
                        throw new DirectoryNotFoundException($"Output directory not found: {dir}");
                    }

                    var pattern = string.IsNullOrWhiteSpace(spec.Pattern) ? "*" : spec.Pattern;
                    var files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                        .Where(IsImage);

                    files = spec.SortBy.ToLowerInvariant() switch
                    {
                        "oldest" => files.OrderBy(f => File.GetLastWriteTimeUtc(f)),
                        "name" => files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
                        _ => files.OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    };

                    var count = ResolveInt(spec.Count, parameters, 1);
                    var picked = files.Take(Math.Max(1, count)).ToList();
                    if (picked.Count == 0)
                    {
                        result.Artifacts.Add(new OutputArtifact("text", null, null,
                            $"No images matching '{pattern}' in {dir}"));
                        break;
                    }

                    foreach (var f in picked)
                    {
                        result.Artifacts.Add(new OutputArtifact("image", f, caption, null));
                    }
                    break;
                }
            case TaskOutputType.LogTail:
                {
                    var path = Resolve(spec.Path!, parameters);
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException($"Log file not found: {path}", path);
                    }

                    var lines = ResolveInt(spec.Lines, parameters, 50);
                    var content = await ReadTailAsync(path, lines, cancellationToken);
                    result.Artifacts.Add(new OutputArtifact("text", null, caption ?? path,
                        Truncate(content, spec.MaxLength)));
                    break;
                }
            default:
                throw new InvalidOperationException($"Unknown output type {spec.Type}");
        }
    }

    private static string Resolve(string template, IReadOnlyDictionary<string, object?> parameters) =>
        ParameterTemplate.Apply(template, parameters);

    private static int ResolveInt(JsonElement? element, IReadOnlyDictionary<string, object?> parameters, int fallback)
    {
        if (element is null) return fallback;

        var el = element.Value;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number when el.TryGetInt32(out var i):
                return i;
            case JsonValueKind.String:
                var rendered = ParameterTemplate.Apply(el.GetString() ?? string.Empty, parameters);
                return int.TryParse(rendered, out var parsed) ? parsed : fallback;
            default:
                return fallback;
        }
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    private static string Truncate(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return text;
        return string.Concat(text.AsSpan(0, max), "\n... (truncated)");
    }

    private static async Task<string> ReadTailAsync(string path, int lines, CancellationToken cancellationToken)
    {
        if (lines <= 0) return string.Empty;

        var queue = new Queue<string>(lines);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (queue.Count == lines) queue.Dequeue();
            queue.Enqueue(line);
        }
        return string.Join('\n', queue);
    }
}
