using System.Text.RegularExpressions;
using TeleTasks.Models;

namespace TeleTasks.Discovery.Detectors;

public static class JustfileDetector
{
    private static readonly Regex RecipeRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_\-]*)\s*(?:\+?[A-Za-z_][A-Za-z0-9_]*\s*)*([A-Za-z0-9_\s\=\'\""\-\+\*\?\,\.]*)\s*:\s*(?:[A-Za-z0-9_\s]*)?$",
        RegexOptions.Compiled);

    private static readonly Regex SimpleHeaderRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_\-]*)\s*([^:]*):", RegexOptions.Compiled);

    private static readonly Regex ParamRegex = new(
        @"([A-Za-z_][A-Za-z0-9_]*)(?:=(?:'([^']*)'|""([^""]*)""|(\S+)))?",
        RegexOptions.Compiled);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        foreach (var name in new[] { "justfile", "Justfile", ".justfile" })
        {
            var path = Path.Combine(projectPath, name);
            if (!File.Exists(path)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith(' ') || line.StartsWith('\t')) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("#")) continue;

                var header = SimpleHeaderRegex.Match(line);
                if (!header.Success) continue;
                if (line.Contains(":=") || line.Contains("::=")) continue;

                var recipe = header.Groups[1].Value;
                if (recipe.StartsWith("_")) continue;

                var paramSpec = header.Groups[2].Value.Trim();
                var parameters = ParseParams(paramSpec);
                var description = LookBehindForComment(lines, i)
                    ?? $"Run `just {recipe}` (justfile recipe).";

                var args = new List<string> { recipe };
                args.AddRange(parameters.Select(p => $"{{{p.Name}}}"));

                yield return new TaskCandidate
                {
                    Source = $"justfile:{recipe}",
                    SuggestedName = TaskCandidate.Sanitize($"just_{recipe}"),
                    Description = description,
                    Command = "/usr/bin/env",
                    Args = new[] { "just", "--justfile", path, "--working-directory", projectPath }
                        .Concat(args.Skip(0))
                        .ToList(),
                    WorkingDirectory = projectPath,
                    Parameters = parameters
                };
            }
        }
    }

    private static List<TaskParameter> ParseParams(string spec)
    {
        var list = new List<TaskParameter>();
        if (string.IsNullOrWhiteSpace(spec)) return list;

        foreach (Match m in ParamRegex.Matches(spec))
        {
            var name = m.Groups[1].Value;
            if (string.IsNullOrEmpty(name)) continue;
            object? defaultValue = null;
            var required = true;
            for (var g = 2; g <= 4; g++)
            {
                if (m.Groups[g].Success && !string.IsNullOrEmpty(m.Groups[g].Value))
                {
                    defaultValue = m.Groups[g].Value;
                    required = false;
                    break;
                }
            }
            list.Add(new TaskParameter
            {
                Name = name,
                Type = "string",
                Required = required,
                Default = defaultValue,
                Description = $"argument '{name}' from justfile recipe"
            });
        }
        return list;
    }

    private static string? LookBehindForComment(string[] lines, int index)
    {
        if (index <= 0) return null;
        var line = lines[index - 1].Trim();
        if (string.IsNullOrEmpty(line) || !line.StartsWith('#')) return null;
        return line.TrimStart('#').Trim();
    }
}
