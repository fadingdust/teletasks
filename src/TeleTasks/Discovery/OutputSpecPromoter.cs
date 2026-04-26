using System.Text.Json;
using System.Text.RegularExpressions;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

/// <summary>
/// Promotes a discovered task's <c>output</c> from the default <c>Text</c> spec
/// to <c>Images</c> / <c>LogTail</c> / <c>File</c> when one of its parameters is
/// strongly named like an output path (<c>output_dir</c>, <c>log_file</c>,
/// <c>output_path</c>, ...).
///
/// The promoted spec is templated against the parameter — so if the user passes
/// a different value at runtime, the output spec follows. When the parameter has
/// no default value (common when scripts interpolate paths internally), the
/// promoter looks in the task's WorkingDirectory for conventional output dirs
/// (<c>outputs/</c>, <c>results/</c>, <c>logs/</c>, ...) and uses the first one
/// that actually contains matching files as the parameter's default.
/// </summary>
public static class OutputSpecPromoter
{
    private static readonly Regex NameSplitter = new(@"[^a-zA-Z]+", RegexOptions.Compiled);

    private static readonly string[] ImageDirGlobs =
        { "outputs", "output", "out", "results", "renders", "generated", "samples" };
    private static readonly string[] LogDirGlobs = { "logs", "log" };
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    public static void Promote(IEnumerable<TaskCandidate> candidates, Action<string>? log = null)
    {
        foreach (var c in candidates) Promote(c, log);
    }

    public static void Promote(TaskCandidate c, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(c.Command))
        {
            log?.Invoke($"{c.SuggestedName}: skipped (no command)");
            return;
        }
        if (c.Output.Type != TaskOutputType.Text)
        {
            log?.Invoke($"{c.SuggestedName}: skipped (output already {c.Output.Type})");
            return;
        }

        TaskParameter? imagesParam = null, logParam = null, fileParam = null;
        var imagesAmbig = false; var logAmbig = false; var fileAmbig = false;

        foreach (var p in c.Parameters)
        {
            switch (Classify(p))
            {
                case OutputKind.ImagesDir:
                    if (imagesParam is not null) imagesAmbig = true; else imagesParam = p;
                    break;
                case OutputKind.LogFile:
                    if (logParam is not null) logAmbig = true; else logParam = p;
                    break;
                case OutputKind.OutputFile:
                    if (fileParam is not null) fileAmbig = true; else fileParam = p;
                    break;
            }
        }

        // Priority: images dir → log file → generic output file. The user's most
        // common ask is "show me the latest renders", so a task that has both an
        // output dir AND a log file gets Images output (the log is still
        // accessible via /jobs / /job N when the task is long-running).
        // Ambiguous cases (multiple params of the same kind) stay as Text.
        if (imagesParam is not null && !imagesAmbig)
        {
            EnsureDefault(imagesParam, c.WorkingDirectory, OutputKind.ImagesDir);
            c.Output = new TaskOutputSpec
            {
                Type = TaskOutputType.Images,
                Directory = $"{{{imagesParam.Name}}}",
                SortBy = "newest",
                Count = JsonSerializer.SerializeToElement(4)
            };
            var (cf, reason) = ApplySidecarDetection(c.Output, imagesParam.Default?.ToString());
            log?.Invoke(
                $"{c.SuggestedName}: Images <- param '{imagesParam.Name}' " +
                $"(default={imagesParam.Default ?? "(none)"}, captionFrom={(cf is not null ? "yes" : "no")} — {reason})");
            return;
        }

        if (logParam is not null && !logAmbig)
        {
            EnsureDefault(logParam, c.WorkingDirectory, OutputKind.LogFile);
            c.Output = new TaskOutputSpec
            {
                Type = TaskOutputType.LogTail,
                Path = $"{{{logParam.Name}}}",
                Lines = JsonSerializer.SerializeToElement(100),
                Caption = $"{{{logParam.Name}}}"
            };
            log?.Invoke($"{c.SuggestedName}: LogTail <- param '{logParam.Name}'");
            return;
        }

        if (fileParam is not null && !fileAmbig)
        {
            EnsureDefault(fileParam, c.WorkingDirectory, OutputKind.OutputFile);
            c.Output = new TaskOutputSpec
            {
                Type = TaskOutputType.File,
                Path = $"{{{fileParam.Name}}}",
                Caption = $"{{{fileParam.Name}}}"
            };
            log?.Invoke($"{c.SuggestedName}: File <- param '{fileParam.Name}'");
            return;
        }

        if (imagesAmbig || logAmbig || fileAmbig)
        {
            log?.Invoke($"{c.SuggestedName}: skipped (ambiguous: multiple output-shaped params)");
        }
        else
        {
            var paramNames = c.Parameters.Count == 0 ? "(no params)" : string.Join(", ", c.Parameters.Select(p => p.Name));
            log?.Invoke($"{c.SuggestedName}: skipped (no output-shaped param recognised — params: {paramNames})");
        }
    }

    private static OutputKind Classify(TaskParameter p)
    {
        var tokens = NameSplitter.Split(p.Name)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        var hasOutputContext = tokens.Overlaps(new[]
        {
            "output", "out", "outputs",
            "dest", "destination", "destinations",
            "result", "results",
            "render", "renders",
            "checkpoint", "checkpoints",
            "save", "saves",
            "write", "writes"
        });
        var hasLogContext = tokens.Contains("log") || tokens.Contains("logs");
        var hasDirSuffix = tokens.Contains("dir") || tokens.Contains("directory") ||
                           tokens.Contains("folder") || tokens.Contains("folders");
        var hasFileSuffix = tokens.Contains("file") || tokens.Contains("files");
        var hasPathSuffix = tokens.Contains("path") || tokens.Contains("paths");

        // log_file / log_path / logfile / logpath — but NOT log_dir which is ambiguous
        // (could be many log files).
        if (hasLogContext && (hasFileSuffix || hasPathSuffix))
            return OutputKind.LogFile;

        // output_dir / out_dir / output_directory / results_dir / renders_folder / ...
        if (hasOutputContext && hasDirSuffix)
            return OutputKind.ImagesDir;

        // Bare names that are unambiguous on their own.
        var bare = p.Name.ToLowerInvariant().Replace('-', '_');
        if (bare is "output" or "outputs" or "results" or "renders" or "samples" or "out" or "outdir")
            return OutputKind.ImagesDir;

        // output_file / output_path / out_path / dest_file ...
        if (hasOutputContext && (hasFileSuffix || hasPathSuffix))
            return OutputKind.OutputFile;

        return OutputKind.None;
    }

    /// <summary>
    /// If the resolved images directory has paired <c>image.ext</c> + sidecar
    /// files (commonly <c>.json</c> for ML/render workflows), wire up
    /// <see cref="TaskOutputSpec.CaptionFrom"/> with auto-diff so each image's
    /// caption surfaces only what changes between renders.
    /// </summary>
    private static (CaptionFromSpec? Spec, string? Reason) ApplySidecarDetection(TaskOutputSpec spec, string? defaultPath)
    {
        if (string.IsNullOrWhiteSpace(defaultPath))
            return (null, "no default path to scan");

        var literal = TeleTasks.Services.PathGlob.ContainsGlob(defaultPath)
            ? TeleTasks.Services.PathGlob.ResolveDirectory(defaultPath)
            : (Directory.Exists(defaultPath) ? defaultPath : null);
        if (literal is null)
            return (null, $"glob {defaultPath} matched nothing on disk");

        try
        {
            var images = Directory.EnumerateFiles(literal, "*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ImageExtensions.Contains(ext);
                })
                .Take(20)
                .ToList();
            if (images.Count == 0)
                return (null, $"{literal} has no images directly (sidecars wouldn't apply)");

            // For each candidate sidecar extension, check whether at least
            // half the images have a paired sidecar — that's the threshold
            // for assuming the dir really uses sidecars.
            var pairCounts = new List<(string Ext, int Count)>();
            foreach (var sidecarExt in new[] { ".json", ".txt", ".yaml", ".yml" })
            {
                var paired = 0;
                foreach (var img in images)
                {
                    var sib = Path.ChangeExtension(img, sidecarExt);
                    if (File.Exists(sib)) paired++;
                }
                pairCounts.Add((sidecarExt, paired));
                if (paired * 2 >= images.Count)
                {
                    spec.CaptionFrom = new CaptionFromSpec
                    {
                        Sidecar = sidecarExt,
                        Mode = "auto-diff"
                    };
                    return (spec.CaptionFrom, $"detected {paired}/{images.Count} {sidecarExt} sidecars in {literal}");
                }
            }
            var summary = string.Join(", ", pairCounts.Select(p => $"{p.Count}{p.Ext}"));
            return (null,
                $"{literal} has {images.Count} image(s) but no matching sidecars (paired counts: {summary})");
        }
        catch (UnauthorizedAccessException) { return (null, "permission denied"); }
        catch (DirectoryNotFoundException) { return (null, "directory not found"); }
    }

    private static void EnsureDefault(TaskParameter p, string? workingDirectory, OutputKind kind)
    {
        if (!string.IsNullOrWhiteSpace(p.Default?.ToString())) return;
        if (string.IsNullOrWhiteSpace(workingDirectory)) return;

        var found = FindGlobCandidate(workingDirectory, kind);
        if (found is not null) p.Default = found;
    }

    private static string? FindGlobCandidate(string workingDirectory, OutputKind kind)
    {
        try
        {
            switch (kind)
            {
                case OutputKind.ImagesDir:
                    foreach (var name in ImageDirGlobs)
                    {
                        var path = Path.Combine(workingDirectory, name);
                        if (!Directory.Exists(path)) continue;

                        // If images live directly here, use the literal path.
                        if (HasImageFileShallow(path)) return path;

                        // Otherwise walk inward. If we find images in a nested
                        // dir, build a glob that replaces variable segments
                        // (anything that isn't a recognised convention name)
                        // with `*`. So results/lora-foo/output/*.png becomes
                        // results/*/output, which PathGlob expands at runtime.
                        var deep = FindDeepImageDir(path, depth: 3);
                        if (deep is null) continue;
                        var globbed = BuildGlobPath(path, deep);
                        return globbed;
                    }
                    break;

                case OutputKind.LogFile:
                    foreach (var dir in LogDirGlobs)
                    {
                        var path = Path.Combine(workingDirectory, dir);
                        if (!Directory.Exists(path)) continue;
                        var newest = Directory.EnumerateFiles(path, "*.log", SearchOption.TopDirectoryOnly)
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                        if (newest is not null) return newest;
                    }
                    {
                        var newest = Directory.EnumerateFiles(workingDirectory, "*.log", SearchOption.TopDirectoryOnly)
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                        if (newest is not null) return newest;
                    }
                    break;

                case OutputKind.OutputFile:
                    // No reasonable default for a generic output file.
                    break;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return null;
    }

    private static bool HasImageFileShallow(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).Take(50))
            {
                if (ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        return false;
    }

    /// <summary>
    /// Walk inward from <paramref name="root"/> up to <paramref name="depth"/> levels
    /// looking for the first directory that contains image files directly.
    /// Returns the absolute path to that directory, or null if none found.
    /// </summary>
    private static string? FindDeepImageDir(string root, int depth)
    {
        if (HasImageFileShallow(root)) return root;
        if (depth <= 0) return null;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(root).Take(20))
            {
                var found = FindDeepImageDir(sub, depth - 1);
                if (found is not null) return found;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        return null;
    }

    /// <summary>
    /// Given a stable root (e.g. <c>/proj/results</c>) and a deeper image dir
    /// (<c>/proj/results/lora-foo/output</c>), produce a glob that replaces
    /// segments that look like instance names (lora-foo) with <c>*</c> while
    /// keeping segments that match a recognised convention name (output) literal:
    /// <c>/proj/results/*/output</c>. PathGlob expands this at runtime.
    /// </summary>
    private static string BuildGlobPath(string root, string deepDir)
    {
        var rel = Path.GetRelativePath(root, deepDir);
        if (string.IsNullOrEmpty(rel) || rel == ".") return root;

        var conventions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ImageDirGlobs) conventions.Add(n);

        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        var rebuilt = segments.Select(seg => conventions.Contains(seg) ? seg : "*").ToArray();
        return Path.Combine(root, string.Join(Path.DirectorySeparatorChar, rebuilt));
    }

    private enum OutputKind { None, ImagesDir, LogFile, OutputFile }
}
