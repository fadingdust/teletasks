using System.Text.RegularExpressions;

namespace TeleTasks.Discovery.Detectors;

public static class MakefileDetector
{
    private static readonly Regex TargetRegex = new(@"^([A-Za-z_][A-Za-z0-9_\-\.]*)\s*:(?!=)", RegexOptions.Compiled);
    private static readonly Regex PhonyRegex = new(@"^\.PHONY\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        foreach (var name in new[] { "Makefile", "makefile", "GNUmakefile" })
        {
            var path = Path.Combine(projectPath, name);
            if (!File.Exists(path)) continue;

            var lines = File.ReadAllLines(path);
            var phony = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = PhonyRegex.Match(lines[i]);
                if (!match.Success) continue;
                foreach (var t in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    phony.Add(t.Trim());
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                if (raw.StartsWith('\t')) continue;

                var match = TargetRegex.Match(raw);
                if (!match.Success) continue;

                var target = match.Groups[1].Value;
                if (target.StartsWith('.')) continue;

                var description = LookBehindForComment(lines, i);
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = $"Run `make {target}` ({Path.GetFileName(path)} target).";
                }

                yield return new TaskCandidate
                {
                    Source = $"Makefile:{target}",
                    SuggestedName = TaskCandidate.Sanitize($"make_{target}"),
                    Description = description,
                    Command = "/usr/bin/make",
                    Args = new List<string> { "-C", projectPath, target },
                    WorkingDirectory = projectPath
                };
            }
        }
    }

    private static string? LookBehindForComment(string[] lines, int index)
    {
        if (index <= 0) return null;
        var line = lines[index - 1].Trim();
        if (string.IsNullOrEmpty(line) || !line.StartsWith('#')) return null;
        return line.TrimStart('#').Trim();
    }
}
