using System.Text.RegularExpressions;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

/// <summary>
/// Lightweight post-processor: for each TaskCandidate parameter whose name looks
/// path-shaped AND has a default value, stat the default and append a short note
/// to the candidate's description so the user sees current state next to the
/// task ("output_dir=dir, 12 .png files, latest 5m ago" etc.).
///
/// No recursion, no LLM, no extra filesystem walks beyond the default values
/// the candidates already declare. Caps per-directory enumeration so a wildly
/// large dir can't slow discovery to a crawl.
/// </summary>
public static class PathInspector
{
    private static readonly Regex NameSplitter = new(@"[^a-zA-Z]+", RegexOptions.Compiled);

    private static readonly HashSet<string> PathLikeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "paths",
        "dir", "dirs", "directory", "directories",
        "folder", "folders",
        "file", "files",
        "log", "logs",
        "out", "output", "outputs",
        "in", "input", "inputs",
        "src", "source", "sources",
        "dst", "dest", "destination",
        "root", "workdir", "workspace",
        "target", "targets",
        "checkpoint", "checkpoints",
        "result", "results"
    };

    private const int MaxFilesPerDir = 500;

    public static void Enrich(IEnumerable<TaskCandidate> candidates)
    {
        foreach (var c in candidates) Enrich(c);
    }

    public static void Enrich(TaskCandidate candidate)
    {
        var notes = new List<string>();
        foreach (var p in candidate.Parameters)
        {
            if (!IsPathLike(p)) continue;
            var raw = p.Default?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var note = InspectPath(raw, candidate.WorkingDirectory);
            if (note is null) continue;

            notes.Add($"{p.Name}={note}");
        }

        if (notes.Count == 0) return;

        var trimmed = (candidate.Description ?? string.Empty).TrimEnd();
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1];
        candidate.Description = $"{trimmed}. (current state: {string.Join("; ", notes)})";
    }

    private static bool IsPathLike(TaskParameter p)
    {
        // Split parameter name on non-letters so output_dir, output-dir, OutputDir all
        // surface "output" and "dir" as separate tokens to compare against the set.
        foreach (var token in NameSplitter.Split(p.Name))
        {
            if (token.Length == 0) continue;
            if (PathLikeTokens.Contains(token)) return true;
        }

        var help = p.Description ?? string.Empty;
        if (help.Contains("path",      StringComparison.OrdinalIgnoreCase)) return true;
        if (help.Contains("file",      StringComparison.OrdinalIgnoreCase)) return true;
        if (help.Contains("directory", StringComparison.OrdinalIgnoreCase)) return true;
        if (help.Contains("folder",    StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? InspectPath(string raw, string? workingDirectory)
    {
        // Reject obvious non-paths (URLs, bare identifiers used as model names, etc.).
        if (raw.Contains("://", StringComparison.Ordinal)) return null;

        string path;
        if (Path.IsPathRooted(raw)) path = raw;
        else if (raw.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, raw.TrimStart('~').TrimStart('/'));
        }
        else
        {
            var baseDir = workingDirectory ?? Directory.GetCurrentDirectory();
            path = Path.Combine(baseDir, raw);
        }

        try
        {
            if (Directory.Exists(path)) return InspectDirectory(path);
            if (File.Exists(path)) return InspectFile(path);
        }
        catch (Exception ex)
        {
            return $"inspection failed ({ex.GetType().Name})";
        }

        // Cheap heuristic: only mark "missing" when raw really looks path-shaped,
        // so we don't decorate plain string params (model names, prompts) just
        // because their parameter name happens to match.
        if (raw.Contains('/') || raw.StartsWith('.') || raw.StartsWith('~'))
            return "missing";
        return null;
    }

    private static string InspectDirectory(string path)
    {
        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Take(MaxFilesPerDir + 1)
                .ToList();

            if (files.Count == 0) return "dir, empty";

            var capped = files.Count > MaxFilesPerDir;
            if (capped) files = files.Take(MaxFilesPerDir).ToList();

            var latest = files.Select(f => File.GetLastWriteTimeUtc(f)).Max();
            var byExt = files
                .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Count()}{(string.IsNullOrEmpty(g.Key) ? "" : g.Key)}");

            var summary = $"dir, {files.Count}{(capped ? "+" : "")} file(s) [{string.Join(", ", byExt)}], latest {RelTime(latest)} ago";
            return summary;
        }
        catch (UnauthorizedAccessException)
        {
            return "dir, no read access";
        }
    }

    private static string InspectFile(string path)
    {
        var info = new FileInfo(path);
        return $"file, {FormatSize(info.Length)}, modified {RelTime(info.LastWriteTimeUtc)} ago";
    }

    private static string RelTime(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 0) return "in the future";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
