namespace TeleTasks.Services;

/// <summary>
/// Filesystem-glob expansion for the directory/path fields of TaskOutputSpec.
/// Walks a path segment-by-segment, expanding any segment that contains
/// <c>*</c> or <c>?</c> against the filesystem, and returns the freshest
/// matching path (by last-write-time). Multi-segment globs work, so
/// <c>results/*-checkpoint/output</c> resolves as expected.
///
/// If the input path has no wildcards it's returned unchanged when it
/// exists, or null otherwise. If no match exists, returns null so callers
/// can produce a clear error.
/// </summary>
public static class PathGlob
{
    public static bool ContainsGlob(string path) =>
        !string.IsNullOrEmpty(path) && (path.Contains('*') || path.Contains('?'));

    public static string? ResolveDirectory(string pattern) => Resolve(pattern, expectDirectory: true);
    public static string? ResolveFile(string pattern) => Resolve(pattern, expectDirectory: false);

    private static string? Resolve(string pattern, bool expectDirectory)
    {
        if (!ContainsGlob(pattern))
        {
            if (expectDirectory) return Directory.Exists(pattern) ? pattern : null;
            return File.Exists(pattern) ? pattern : null;
        }

        var matches = ExpandPath(pattern);
        if (matches.Count == 0) return null;

        var filtered = expectDirectory
            ? matches.Where(Directory.Exists)
            : matches.Where(File.Exists);

        return filtered
            .OrderByDescending(p => GetLastWriteSafe(p))
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> ExpandPath(string pattern)
    {
        var separator = Path.DirectorySeparatorChar;
        var segments = pattern.Split(new[] { '/', '\\' });
        if (segments.Length == 0) return Array.Empty<string>();

        // Seed with the leading segment. For absolute Linux paths the first
        // segment is the empty string before the leading '/', which means we
        // start from "/". For relative paths we start from cwd.
        List<string> current;
        var first = segments[0];
        if (first.Length == 0)
        {
            current = new List<string> { separator.ToString() };
        }
        else if (Path.IsPathRooted(first + separator))
        {
            current = new List<string> { first + separator };
        }
        else
        {
            current = new List<string> { first };
        }

        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0) continue;

            var next = new List<string>(current.Count);
            if (!ContainsGlob(segment))
            {
                foreach (var c in current)
                {
                    next.Add(Path.Combine(c, segment));
                }
            }
            else
            {
                foreach (var c in current)
                {
                    if (!Directory.Exists(c)) continue;
                    IEnumerable<string> subdirs;
                    try
                    {
                        subdirs = Directory.EnumerateDirectories(c, segment, SearchOption.TopDirectoryOnly);
                    }
                    catch (UnauthorizedAccessException) { continue; }

                    // The final segment can also match files (e.g. results/*.png).
                    if (i == segments.Length - 1)
                    {
                        IEnumerable<string> matchedFiles;
                        try
                        {
                            matchedFiles = Directory.EnumerateFiles(c, segment, SearchOption.TopDirectoryOnly);
                        }
                        catch (UnauthorizedAccessException) { matchedFiles = Array.Empty<string>(); }
                        next.AddRange(subdirs);
                        next.AddRange(matchedFiles);
                    }
                    else
                    {
                        next.AddRange(subdirs);
                    }
                }
            }
            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }

    private static DateTime GetLastWriteSafe(string path)
    {
        try
        {
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        }
        catch { }
        return DateTime.MinValue;
    }
}
