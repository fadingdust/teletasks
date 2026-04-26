using System.Text.RegularExpressions;

namespace TeleTasks.Discovery.Detectors;

public static class MakefileDetector
{
    private static readonly Regex TargetRegex = new(@"^([A-Za-z_][A-Za-z0-9_\-\.]*)\s*:(?!=)", RegexOptions.Compiled);
    private static readonly Regex PhonyRegex = new(@"^\.PHONY\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        var scope = TaskCandidate.ProjectScope(projectPath);
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
                    Source = $"Makefile:{scope}:{target}",
                    SuggestedName = TaskCandidate.Sanitize($"make_{scope}_{target}"),
                    Description = description,
                    Command = "/usr/bin/make",
                    Args = new List<string> { "-C", projectPath, target },
                    WorkingDirectory = projectPath,
                    SourceText = ExtractTargetBody(lines, i)
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

    private static string? ExtractTargetBody(string[] lines, int targetLine)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(lines[targetLine]);
        for (var i = targetLine + 1; i < lines.Length && i < targetLine + 30; i++)
        {
            var line = lines[i];
            // Recipe lines are tab-indented; blank lines or another rule end the recipe.
            if (line.StartsWith('\t')) sb.AppendLine(line);
            else if (string.IsNullOrWhiteSpace(line)) continue;
            else break;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
