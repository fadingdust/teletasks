using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using TeleTasks.Discovery.Detectors;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

/// <summary>
/// When a shell script's body invokes a Python script we've also discovered,
/// the shell wrapper inherits the Python script's <c>output</c> spec — so a
/// <c>run.sh</c> that wraps <c>app.py</c> sends back the same images +
/// sidecars as the bare Python task would.
///
/// Detection strategy: pull every <c>*.py</c> token out of the shell's source
/// text, then look up the first one that matches a known argparse-discovered
/// candidate. This covers all the common invocation styles:
///   python3 app.py ...        # plain
///   python -u app.py          # unbuffered
///   pipenv run python app.py  # pipenv
///   poetry run python app.py  # poetry
///   uv run app.py             # uv
///   $PYTHON app.py            # env-var python
///   ./app.py                  # shebang executable
///   exec app.py               # exec'd
///
/// Runs after PathInspector + OutputSpecPromoter so the python candidate
/// already has its promoted/auto-diff output spec to copy from.
/// </summary>
public static class ShellWrapperResolver
{
    // Match any path-shaped token ending in .py — captures filenames anywhere
    // they appear in the script. We filter against the candidate map after.
    private static readonly Regex PyFileToken = new(
        @"(?<![A-Za-z0-9_])([A-Za-z0-9_./\-]+\.py)\b",
        RegexOptions.Compiled);

    public static void Resolve(IReadOnlyList<TaskCandidate> candidates, Action<string>? log = null)
    {
        var pyByFilename = new Dictionary<string, TaskCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!c.Source.StartsWith("py:argparse:", StringComparison.Ordinal)) continue;
            var pyFile = c.Source[(c.Source.LastIndexOf(':') + 1)..];
            pyByFilename[pyFile] = c;
        }
        if (pyByFilename.Count == 0) return;

        foreach (var shell in candidates)
        {
            if (!shell.Source.StartsWith("sh:", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(shell.SourceText))
            {
                log?.Invoke($"{shell.SuggestedName}: no source text on candidate");
                continue;
            }

            var pyTokens = PyFileToken.Matches(shell.SourceText)
                .Select(m => m.Groups[1].Value)
                .ToList();
            var matchedPy = FindMatchingPy(pyTokens, pyByFilename);
            if (matchedPy is null)
            {
                // Filename-based lookup missed. The token might point at a
                // Python file in a subdirectory that's below the top-level
                // discovery floor (e.g. `python scripts/foo.py`). Try to
                // resolve the path against the shell's working dir and scan
                // that file directly. The result is used only as the wrap
                // target — it's intentionally NOT added to the main candidate
                // list so the catalogue stays "top-level scripts only".
                matchedPy = TryLazyDeepScan(shell, pyTokens, log);
            }
            if (matchedPy is null)
            {
                if (pyTokens.Count == 0)
                {
                    var previewLen = Math.Min(120, shell.SourceText.Length);
                    var preview = shell.SourceText[..previewLen].Replace('\n', ' ');
                    log?.Invoke(
                        $"{shell.SuggestedName}: no *.py tokens found in script body " +
                        $"(scanned all {shell.SourceText.Length} chars; first {previewLen}: {preview})");
                }
                else
                {
                    log?.Invoke(
                        $"{shell.SuggestedName}: found .py tokens [{string.Join(", ", pyTokens.Distinct())}] " +
                        $"but none matched candidate map [{string.Join(", ", pyByFilename.Keys)}] " +
                        $"and none resolved to a readable file under {shell.WorkingDirectory ?? "(no workingDir)"}");
                }
                continue;
            }

            if (matchedPy.Output.Type == TaskOutputType.Text)
            {
                log?.Invoke($"{shell.SuggestedName} -> {matchedPy.SuggestedName}: target's output is still Text (promoter didn't fire) — nothing to inherit");
                continue;
            }

            if (shell.Output.Type != TaskOutputType.Text)
            {
                log?.Invoke($"{shell.SuggestedName}: already has non-Text output (kept)");
                continue;
            }

            shell.Output = Clone(matchedPy.Output);

            var copied = CopyTemplatedParameters(shell, matchedPy);

            var note = $"wraps {matchedPy.SuggestedName}";
            if (!shell.Description.Contains(note, StringComparison.OrdinalIgnoreCase))
            {
                shell.Description = string.IsNullOrWhiteSpace(shell.Description)
                    ? $"({note})"
                    : $"{shell.Description} ({note})";
            }
            log?.Invoke(
                $"{shell.SuggestedName} <- {matchedPy.SuggestedName} " +
                $"({matchedPy.Output.Type}, captionFrom={(matchedPy.Output.CaptionFrom is not null ? "yes" : "no")}, " +
                $"params copied={(copied.Count == 0 ? "none" : string.Join(",", copied))})");
        }
    }

    private static IReadOnlyList<string> CopyTemplatedParameters(TaskCandidate shell, TaskCandidate py)
    {
        var referenced = ExtractReferencedNames(shell.Output);
        var copied = new List<string>();
        foreach (var name in referenced)
        {
            if (shell.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var pyParam = py.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (pyParam is null) continue;

            shell.Parameters.Add(new TaskParameter
            {
                Name = pyParam.Name,
                Type = pyParam.Type,
                Required = false,
                Default = pyParam.Default,
                Description = string.IsNullOrWhiteSpace(pyParam.Description)
                    ? $"inherited from {py.SuggestedName}"
                    : $"{pyParam.Description} (inherited from {py.SuggestedName})",
                Enum = pyParam.Enum
            });
            copied.Add(name);
        }
        return copied;
    }

    private static readonly Regex NameRef = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static IEnumerable<string> ExtractReferencedNames(TaskOutputSpec spec)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanForNames(spec.Directory, names);
        ScanForNames(spec.Path, names);
        ScanForNames(spec.Caption, names);
        ScanForNames(spec.Pattern, names);
        if (spec.CaptionFrom is { } cf) ScanForNames(cf.Template, names);
        ScanJsonElement(spec.Count, names);
        ScanJsonElement(spec.Lines, names);
        return names;

        static void ScanForNames(string? value, HashSet<string> bag)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (Match m in NameRef.Matches(value)) bag.Add(m.Groups[1].Value);
        }

        static void ScanJsonElement(JsonElement? element, HashSet<string> bag)
        {
            if (element is null) return;
            if (element.Value.ValueKind == JsonValueKind.String)
                ScanForNames(element.Value.GetString(), bag);
        }
    }

    private static TaskCandidate? FindMatchingPy(IReadOnlyList<string> pyTokens, Dictionary<string, TaskCandidate> pyByFilename)
    {
        foreach (var token in pyTokens)
        {
            var fileName = Path.GetFileName(token);
            if (pyByFilename.TryGetValue(fileName, out var match))
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve each .py token as a path relative to the shell script's
    /// working directory. The first one that points at a real file gets
    /// scanned through <see cref="ArgparsePythonDetector.DetectFromFile"/>
    /// and promoted in isolation, then returned as the wrap target.
    /// Skips tokens whose only segment is bare like <c>setup.py</c> when
    /// no such file exists at the working dir root — those would have
    /// been caught by the top-level filename lookup if they existed.
    /// </summary>
    private static TaskCandidate? TryLazyDeepScan(
        TaskCandidate shell,
        IReadOnlyList<string> pyTokens,
        Action<string>? log)
    {
        var root = shell.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root)) return null;

        foreach (var token in pyTokens.Distinct(StringComparer.Ordinal))
        {
            // Only follow tokens that actually look like a path (have a
            // separator) — bare filenames were already covered by the
            // candidate-map lookup in the caller.
            if (!token.Contains('/') && !token.Contains('\\')) continue;

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(token)
                    ? token
                    : Path.GetFullPath(Path.Combine(root, token));
            }
            catch { continue; }
            if (!File.Exists(resolved)) continue;

            // Use the resolved file's parent dir as the working directory
            // so the scanned candidate's relative output paths still anchor
            // sensibly (the Python script is invoked from somewhere — most
            // commonly the shell's CWD, which is already root).
            var deep = ArgparsePythonDetector.DetectFromFile(resolved, root);
            if (deep is null)
            {
                log?.Invoke($"{shell.SuggestedName}: token '{token}' resolved to {resolved} but argparse scan failed");
                continue;
            }

            // Promote in isolation so this lazy candidate gets its output
            // spec auto-derived (Images / sidecars / glob fallback) before
            // we copy it onto the shell wrapper.
            OutputSpecPromoter.Promote(deep, log);

            log?.Invoke($"{shell.SuggestedName}: deep-scanned {resolved} -> {deep.SuggestedName} (output={deep.Output.Type})");
            return deep;
        }
        return null;
    }

    private static TaskOutputSpec Clone(TaskOutputSpec spec)
    {
        var json = JsonSerializer.Serialize(spec);
        return JsonSerializer.Deserialize<TaskOutputSpec>(json) ?? new TaskOutputSpec();
    }
}
