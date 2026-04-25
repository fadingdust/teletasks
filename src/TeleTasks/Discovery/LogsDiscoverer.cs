using System.Text.Json;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

public sealed class LogsDiscoverOptions
{
    public string Path { get; set; } = "/var/log";
    public int SinceDays { get; set; } = 7;
    public long MaxBytes { get; set; } = 100L * 1024 * 1024;
    public bool Recursive { get; set; }
    public string Pattern { get; set; } = "*.log";
}

public static class LogsDiscoverer
{
    private static readonly JsonElement DefaultLinesTemplate = JsonSerializer.SerializeToElement("{lines}");

    public static IReadOnlyList<TaskCandidate> Discover(LogsDiscoverOptions options)
    {
        var dir = Path.GetFullPath(options.Path);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"Path not found: {dir}");
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(0, options.SinceDays));
        var files = WalkLogs(dir, options.Pattern, options.Recursive);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TaskCandidate>();

        foreach (var file in files)
        {
            FileInfo info;
            try { info = new FileInfo(file); }
            catch { continue; }

            if (!info.Exists) continue;
            if (info.Length == 0) continue;
            if (info.Length > options.MaxBytes) continue;
            if (info.LastWriteTimeUtc < cutoff) continue;
            if (!CanRead(info.FullName)) continue;

            var baseName = TaskCandidate.Sanitize(Path.GetFileNameWithoutExtension(info.Name));
            if (string.IsNullOrEmpty(baseName)) baseName = "log";
            var name = $"log_{baseName}";
            var dedup = name;
            var i = 2;
            while (!seenNames.Add(dedup)) dedup = $"{name}_{i++}";

            candidates.Add(new TaskCandidate
            {
                Source = $"log:{info.FullName}",
                SuggestedName = dedup,
                Description = $"Tail `{info.FullName}` (last modified {info.LastWriteTime:yyyy-MM-dd HH:mm}, {FormatSize(info.Length)}).",
                Parameters = new List<TaskParameter>
                {
                    new() { Name = "lines", Type = "integer", Default = 100L, Description = "How many lines to tail" }
                },
                Output = new TaskOutputSpec
                {
                    Type = TaskOutputType.LogTail,
                    Path = info.FullName,
                    Lines = DefaultLinesTemplate,
                    Caption = info.FullName
                }
            });
        }

        return candidates;
    }

    private static IEnumerable<string> WalkLogs(string dir, string pattern, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(current, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var f in files) yield return f;

            if (!recursive) continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(current); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var s in subdirs) stack.Push(s);
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[1];
            return fs.Read(buf, 0, 1) >= 0;
        }
        catch { return false; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
