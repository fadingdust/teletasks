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
                    var rendered = ResolveAndAnchor(spec.Path!, task, parameters);
                    var path = ResolveFileGlob(rendered);
                    if (path is null)
                    {
                        throw new FileNotFoundException(GlobError(rendered, "Output file"), rendered);
                    }
                    result.Artifacts.Add(new OutputArtifact("file", path, caption, null));
                    break;
                }
            case TaskOutputType.Image:
                {
                    var rendered = ResolveAndAnchor(spec.Path!, task, parameters);
                    var path = ResolveFileGlob(rendered);
                    if (path is null)
                    {
                        throw new FileNotFoundException(GlobError(rendered, "Output image"), rendered);
                    }
                    EmitImagesWithSidecars(spec, new[] { path }, caption, rendered, result);
                    break;
                }
            case TaskOutputType.Images:
                {
                    var rendered = ResolveAndAnchor(spec.Directory!, task, parameters);
                    var dir = ResolveDirectoryGlob(rendered);
                    if (dir is null)
                    {
                        throw new DirectoryNotFoundException(
                            PathGlob.ContainsGlob(rendered)
                                ? $"Output directory glob matched nothing: {rendered}"
                                : $"Output directory not found: {rendered}");
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

                    EmitImagesWithSidecars(spec, picked, caption, rendered, result);
                    break;
                }
            case TaskOutputType.LogTail:
                {
                    var rendered = ResolveAndAnchor(spec.Path!, task, parameters);
                    var path = ResolveFileGlob(rendered);
                    if (path is null)
                    {
                        throw new FileNotFoundException(GlobError(rendered, "Log file"), rendered);
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

    private void EmitImagesWithSidecars(
        TaskOutputSpec spec,
        IReadOnlyList<string> images,
        string? defaultCaption,
        string? renderedDirectoryPattern,
        TaskExecutionResult result)
    {
        // If the directory was a glob (e.g. ./results/*/output), capture the
        // stable prefix so we can show users which expanded match the file
        // came from. Without this, "the latest from results/lora-foo/output"
        // and "the latest from results/lora-bar/output" look identical.
        var stablePrefix = renderedDirectoryPattern is not null
            && PathGlob.ContainsGlob(renderedDirectoryPattern)
                ? StablePrefix(renderedDirectoryPattern)
                : null;

        var captionFrom = spec.CaptionFrom;
        var sidecarExt = captionFrom?.Sidecar;
        var hasSidecars = !string.IsNullOrWhiteSpace(sidecarExt);
        var captionMaxLen = captionFrom?.MaxLength ?? 1000;
        var mode = (captionFrom?.Mode ?? "auto-diff").ToLowerInvariant();

        // auto-diff needs ≥ 2 sidecars to compute constant-vs-variable
        // meaningfully. With one image, fall back to verbatim so the user
        // still sees every field instead of an empty caption.
        if (mode == "auto-diff" && images.Count <= 1)
        {
            _logger.LogInformation(
                "auto-diff requested but only {Count} image(s) — falling back to verbatim caption.",
                images.Count);
            mode = "verbatim";
        }

        if (captionFrom is null)
        {
            _logger.LogInformation(
                "Sending {Count} image(s) with no captionFrom on the spec — captions will be empty.",
                images.Count);
        }
        else if (!hasSidecars)
        {
            _logger.LogInformation(
                "captionFrom present but sidecar extension is empty/whitespace — captions will be empty.");
        }
        else
        {
            _logger.LogInformation(
                "captionFrom: sidecar='{Ext}', mode='{Mode}', images={Count}",
                sidecarExt, mode, images.Count);
        }

        SidecarMetadata.SidecarBatch? batch = null;
        if (hasSidecars)
        {
            // Per-image sidecar resolution log so misses are visible.
            foreach (var img in images)
            {
                var resolved = SidecarMetadata.SiblingPath(img, sidecarExt!);
                var hit = File.Exists(resolved);
                _logger.LogInformation(
                    "  sidecar for {Image}: tried {Resolved} ({Status})",
                    Path.GetFileName(img), resolved, hit ? "found" : "MISSING");
            }

            batch = SidecarMetadata.Read(images, sidecarExt!);

            var totalKeys = batch.Constant.Count + (batch.Variable.Count > 0 ? batch.Variable[0].Count : 0);
            _logger.LogInformation(
                "  sidecar diff: {Constant} constant field(s), {Variable} variable field(s) per image",
                batch.Constant.Count,
                batch.Variable.Count > 0 ? batch.Variable[0].Count : 0);
            if (totalKeys == 0)
            {
                _logger.LogWarning(
                    "  no scalar fields read from any sidecar — captions will be empty. " +
                    "Sidecars exist? {Existing}/{Total}. JSON parse failures or non-scalar top-level values are silently dropped.",
                    batch.Full.Count(d => d.Count > 0), images.Count);
            }

            if (mode == "auto-diff" && batch.Constant.Count > 0 && images.Count > 1)
            {
                var header = "Shared: " + SidecarMetadata.BuildCaption(
                    batch.Constant, template: null, maxLength: spec.MaxLength);
                result.Artifacts.Add(new OutputArtifact("text", null, null, header));
            }
        }

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            string? caption = defaultCaption;

            // Prepend the relative path within the glob-expanded portion of the
            // tree so the user can tell which match the file came from.
            if (stablePrefix is not null)
            {
                var rel = RelativeFrom(stablePrefix, image);
                if (!string.IsNullOrEmpty(rel))
                {
                    caption = string.IsNullOrWhiteSpace(caption)
                        ? rel
                        : $"{rel}\n{caption}";
                }
            }

            if (hasSidecars && batch is not null)
            {
                IReadOnlyDictionary<string, string> fields = mode switch
                {
                    "verbatim" => batch.Full[i],
                    "template" => batch.Full[i],
                    _          => batch.Variable[i]
                };
                var fromSidecar = SidecarMetadata.BuildCaption(fields, captionFrom!.Template, captionMaxLen);
                if (!string.IsNullOrWhiteSpace(fromSidecar))
                {
                    caption = string.IsNullOrWhiteSpace(caption)
                        ? fromSidecar
                        : $"{caption}\n{fromSidecar}";
                }
            }

            result.Artifacts.Add(new OutputArtifact("image", image, caption, null));

            if (spec.Siblings is { Count: > 0 } siblings)
            {
                foreach (var ext in siblings)
                {
                    var sibling = SidecarMetadata.SiblingPath(image, ext);
                    if (File.Exists(sibling))
                    {
                        result.Artifacts.Add(new OutputArtifact("file", sibling, null, null));
                    }
                }
            }
        }
    }

    private static string Resolve(string template, IReadOnlyDictionary<string, object?> parameters) =>
        ParameterTemplate.Apply(template, parameters);

    /// <summary>
    /// Resolve a template into a path AND anchor relative results against the
    /// task's WorkingDirectory. Without this, relative paths like
    /// <c>./outputs</c> resolve against the bot's CWD — but the script that
    /// produced the files runs in <c>task.WorkingDirectory</c>, so the
    /// collector would look in the wrong place.
    /// </summary>
    private string ResolveAndAnchor(
        string template,
        TaskDefinition task,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var rendered = ParameterTemplate.Apply(template, parameters);
        if (string.IsNullOrEmpty(rendered)) return rendered;
        if (Path.IsPathRooted(rendered)) return rendered;
        if (string.IsNullOrWhiteSpace(task.WorkingDirectory)) return rendered;

        var workingDir = ParameterTemplate.Apply(task.WorkingDirectory, parameters);
        if (string.IsNullOrWhiteSpace(workingDir)) return rendered;

        var combined = Path.Combine(workingDir, rendered);
        _logger.LogDebug("Anchored relative output path '{Template}' to '{Combined}' via task workingDirectory '{WorkingDir}'",
            template, combined, workingDir);
        return combined;
    }

    private string? ResolveDirectoryGlob(string rendered)
    {
        if (!PathGlob.ContainsGlob(rendered))
        {
            return Directory.Exists(rendered) ? rendered : null;
        }
        var resolved = PathGlob.ResolveDirectory(rendered);
        if (resolved is not null)
        {
            _logger.LogInformation("Glob {Pattern} → {Resolved}", rendered, resolved);
        }
        return resolved;
    }

    private string? ResolveFileGlob(string rendered)
    {
        if (!PathGlob.ContainsGlob(rendered))
        {
            return File.Exists(rendered) ? rendered : null;
        }
        var resolved = PathGlob.ResolveFile(rendered);
        if (resolved is not null)
        {
            _logger.LogInformation("Glob {Pattern} → {Resolved}", rendered, resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Returns the longest leading directory prefix of a glob pattern that
    /// contains no wildcards. <c>./results/*/output</c> → <c>./results/</c>.
    /// Used to compute the relative tail to show in image captions.
    /// </summary>
    private static string? StablePrefix(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;

        var firstStar = pattern.IndexOf('*');
        var firstQ = pattern.IndexOf('?');
        var first = firstStar < 0 ? firstQ
                    : firstQ < 0 ? firstStar
                    : Math.Min(firstStar, firstQ);
        if (first < 0) return null;

        var sepIdx = pattern.LastIndexOfAny(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, first);
        if (sepIdx < 0) return null;

        return pattern[..(sepIdx + 1)];
    }

    /// <summary>
    /// Path of <paramref name="full"/> relative to <paramref name="prefix"/>.
    /// If <paramref name="full"/> doesn't start with <paramref name="prefix"/>,
    /// falls back to the file's basename so the caption is at least informative.
    /// </summary>
    private static string RelativeFrom(string prefix, string full)
    {
        try
        {
            var prefFull = Path.GetFullPath(prefix);
            var fileFull = Path.GetFullPath(full);
            var rel = Path.GetRelativePath(prefFull, fileFull);
            return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(full) : rel;
        }
        catch
        {
            return Path.GetFileName(full);
        }
    }

    private static string GlobError(string rendered, string label) =>
        PathGlob.ContainsGlob(rendered)
            ? $"{label} glob matched nothing: {rendered}"
            : $"{label} not found: {rendered}";

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
