using System.Text.RegularExpressions;
using TeleTasks.Models;

namespace TeleTasks.Discovery.Detectors;

public static class ShellScriptDetector
{
    private static readonly Regex GetoptsRegex = new(@"getopts\s+[""'](?<spec>[A-Za-z0-9:]+)[""']\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex DefaultParamRegex = new(@"\$\{(?<n>[1-9])\:-(?<def>[^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex BarePositionalRegex = new(@"\$(?<n>[1-9])(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex UsageRegex = new(@"^\s*#\s*Usage\s*:\s*(?<text>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DescriptionRegex = new(@"^\s*#\s*(?:Description|Summary)\s*:\s*(?<text>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CaseFlagRegex = new(@"^\s*-(?<flag>[A-Za-z])\s*\)", RegexOptions.Compiled);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectPath, "*.sh", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0) continue;

            var description = ExtractHeaderDescription(lines)
                ?? $"Run `{Path.GetFileName(file)}`.";

            var allParams = ExtractParameters(lines);
            var positional = allParams.Where(p => p.Name.StartsWith("arg")).ToList();
            var flagParams = allParams.Where(p => !p.Name.StartsWith("arg")).ToList();

            var args = new List<string> { file };
            args.AddRange(positional.Select(p => $"{{{p.Name}}}"));

            if (flagParams.Count > 0)
            {
                var flags = string.Join(", ", flagParams.Select(p => $"-{p.Name}"));
                description = $"{description} (flags: {flags} — edit args to use)";
            }

            var parameters = positional;

            yield return new TaskCandidate
            {
                Source = $"sh:{Path.GetFileName(file)}",
                SuggestedName = TaskCandidate.Sanitize($"sh_{Path.GetFileNameWithoutExtension(file)}"),
                Description = description,
                Command = "/bin/bash",
                Args = args,
                WorkingDirectory = projectPath,
                Parameters = parameters
            };
        }
    }

    private static string? ExtractHeaderDescription(string[] lines)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length && i < 20; i++)
        {
            var line = lines[i];
            if (i == 0 && line.StartsWith("#!")) continue;
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (sb.Length > 0) break;
                continue;
            }
            if (!trimmed.StartsWith('#')) break;

            var usage = UsageRegex.Match(line);
            if (usage.Success) return usage.Groups["text"].Value.Trim();
            var desc = DescriptionRegex.Match(line);
            if (desc.Success) return desc.Groups["text"].Value.Trim();

            var content = trimmed.TrimStart('#').Trim();
            if (content.Length > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(content);
            }
        }
        var combined = sb.ToString().Trim();
        return string.IsNullOrEmpty(combined) ? null : combined;
    }

    private static List<TaskParameter> ExtractParameters(string[] lines)
    {
        var parameters = new Dictionary<string, TaskParameter>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var go = GetoptsRegex.Match(line);
            if (go.Success)
            {
                var spec = go.Groups["spec"].Value;
                for (var i = 0; i < spec.Length; i++)
                {
                    var c = spec[i];
                    if (!char.IsLetter(c)) continue;
                    var hasArg = i + 1 < spec.Length && spec[i + 1] == ':';
                    if (hasArg) i++;
                    parameters.TryAdd(c.ToString(), new TaskParameter
                    {
                        Name = c.ToString(),
                        Type = hasArg ? "string" : "boolean",
                        Required = false,
                        Description = $"-{c} flag"
                    });
                }
                continue;
            }

            var caseFlag = CaseFlagRegex.Match(line);
            if (caseFlag.Success)
            {
                var c = caseFlag.Groups["flag"].Value;
                parameters.TryAdd(c, new TaskParameter
                {
                    Name = c,
                    Type = "string",
                    Required = false,
                    Description = $"-{c} option"
                });
            }
        }

        var maxPositional = 0;
        var defaults = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            foreach (Match m in DefaultParamRegex.Matches(line))
            {
                var n = int.Parse(m.Groups["n"].Value);
                maxPositional = Math.Max(maxPositional, n);
                defaults.TryAdd(n, m.Groups["def"].Value);
            }
            foreach (Match m in BarePositionalRegex.Matches(line))
            {
                var n = int.Parse(m.Groups["n"].Value);
                maxPositional = Math.Max(maxPositional, n);
            }
        }

        for (var n = 1; n <= maxPositional; n++)
        {
            var name = $"arg{n}";
            parameters.TryAdd(name, new TaskParameter
            {
                Name = name,
                Type = "string",
                Required = !defaults.ContainsKey(n),
                Default = defaults.TryGetValue(n, out var d) ? d : null,
                Description = $"positional argument ${n}"
            });
        }

        return parameters.Values.ToList();
    }
}
