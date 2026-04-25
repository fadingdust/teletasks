using System.Text.RegularExpressions;

namespace TeleTasks.Discovery.Detectors;

public static class PyprojectDetector
{
    private static readonly Regex SectionRegex = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex EntryRegex = new(
        @"^\s*(?<key>[A-Za-z_][A-Za-z0-9_\-]*)\s*=\s*""(?<value>[^""]*)""\s*$",
        RegexOptions.Compiled);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        var path = Path.Combine(projectPath, "pyproject.toml");
        if (!File.Exists(path)) yield break;

        string? section = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            var sec = SectionRegex.Match(line);
            if (sec.Success)
            {
                section = sec.Groups["name"].Value.Trim();
                continue;
            }
            if (section is not "project.scripts" and not "tool.poetry.scripts") continue;

            var entry = EntryRegex.Match(line);
            if (!entry.Success) continue;

            var name = entry.Groups["key"].Value;
            var value = entry.Groups["value"].Value;

            yield return new TaskCandidate
            {
                Source = $"pyproject.toml:{section}.{name}",
                SuggestedName = TaskCandidate.Sanitize($"py_{name}"),
                Description = $"Run console script `{name}` ({value}) from pyproject.toml.",
                Command = "/usr/bin/env",
                Args = new List<string> { name },
                WorkingDirectory = projectPath
            };
        }
    }
}
