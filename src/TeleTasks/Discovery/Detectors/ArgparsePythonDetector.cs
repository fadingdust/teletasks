using System.Diagnostics;
using System.Text.Json;
using TeleTasks.Models;

namespace TeleTasks.Discovery.Detectors;

public static class ArgparsePythonDetector
{
    private const string HelperScript = @"
import argparse, ast, json, sys

def lit(node):
    if node is None: return None
    try: return ast.literal_eval(node)
    except Exception: return None

def type_name(node):
    if isinstance(node, ast.Name): return node.id
    return None

def parse_call(call):
    flags = []
    for a in call.args:
        v = lit(a)
        if isinstance(v, str): flags.append(v)
    if not flags: return None

    kw = {}
    for k in call.keywords:
        if k.arg is None: continue
        if k.arg == 'type':
            kw['type'] = type_name(k.value) or lit(k.value)
        else:
            kw[k.arg] = lit(k.value)

    is_positional = not any(f.startswith('-') for f in flags)
    name = kw.get('dest')
    if not name:
        for f in flags:
            if f.startswith('--'):
                name = f[2:].replace('-', '_'); break
        if not name:
            for f in flags:
                if not f.startswith('-'):
                    name = f; break
        if not name:
            name = flags[0].lstrip('-').replace('-', '_')

    action = kw.get('action')
    is_bool = action in ('store_true', 'store_false')

    t = 'string'
    if kw.get('type') == 'int': t = 'integer'
    elif kw.get('type') == 'float': t = 'number'
    elif is_bool: t = 'boolean'

    required = kw.get('required', is_positional)
    if 'default' in kw and kw['default'] is not None:
        required = False

    long_flag = next((f for f in flags if f.startswith('--')), None)
    short_flag = next((f for f in flags if f.startswith('-') and not f.startswith('--')), None)

    return {
        'name': name,
        'type': t,
        'required': bool(required),
        'default': kw.get('default'),
        'help': kw.get('help') or '',
        'choices': kw.get('choices'),
        'is_positional': is_positional,
        'is_boolean_flag': is_bool,
        'long_flag': long_flag,
        'short_flag': short_flag,
    }

def find_parser_vars(tree):
    parser_vars = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Call):
            f = node.value.func
            if (isinstance(f, ast.Attribute) and f.attr == 'ArgumentParser') or \
               (isinstance(f, ast.Name) and f.id == 'ArgumentParser'):
                for tgt in node.targets:
                    if isinstance(tgt, ast.Name): parser_vars.add(tgt.id)
    return parser_vars

def main(path):
    try:
        with open(path) as f: tree = ast.parse(f.read())
    except Exception as e:
        print(json.dumps({'error': str(e)})); return

    parser_vars = find_parser_vars(tree)
    if not parser_vars:
        print(json.dumps({'arguments': []})); return

    args = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            f = node.func
            if isinstance(f, ast.Attribute) and f.attr == 'add_argument':
                if isinstance(f.value, ast.Name) and f.value.id in parser_vars:
                    parsed = parse_call(node)
                    if parsed: args.append(parsed)

    description = ''
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            f = node.func
            if (isinstance(f, ast.Attribute) and f.attr == 'ArgumentParser') or \
               (isinstance(f, ast.Name) and f.id == 'ArgumentParser'):
                for kw in node.keywords:
                    if kw.arg == 'description':
                        v = lit(kw.value)
                        if isinstance(v, str): description = v; break

    print(json.dumps({'description': description, 'arguments': args}, default=str))

if __name__ == '__main__':
    main(sys.argv[1])
";

    private static readonly string[] PythonCandidates = { "python3", "python" };
    private static readonly string[] VenvDirectoryCandidates = { ".venv", "venv", "env" };
    private static readonly Lazy<string?> _resolvedSystemPython = new(ResolvePython);

    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectPath, "*.py", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var file in files)
        {
            var candidate = DetectFromFile(file, projectPath);
            if (candidate is not null) yield return candidate;
        }
    }

    /// <summary>
    /// Probe the project's working directory for a venv layout we recognise
    /// (<c>.venv/bin/python</c> first, then <c>venv/bin/python</c>, then
    /// <c>env/bin/python</c>) and return that absolute path. Falls back to
    /// the memoised system <c>python3</c>/<c>python</c> resolution when no
    /// venv is found. The returned executable is used both for the AST
    /// helper (parse-time) and as the candidate's <c>Command</c> (run-time)
    /// so a project's deps are visible end-to-end.
    /// </summary>
    public static string? ResolveProjectPython(string workingDirectory)
    {
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            foreach (var venv in VenvDirectoryCandidates)
            {
                var candidate = Path.Combine(workingDirectory, venv, "bin", "python");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return _resolvedSystemPython.Value;
    }

    /// <summary>
    /// Run the argparse extractor against a single Python file and return a
    /// candidate, or null if the file isn't a recognisable argparse script.
    /// Used both by top-level <see cref="Detect"/> and on-demand by
    /// <see cref="ShellWrapperResolver"/> when an sh script invokes a Python
    /// file living below the discovery floor (e.g. <c>scripts/foo.py</c>).
    /// </summary>
    public static TaskCandidate? DetectFromFile(string filePath, string workingDirectory)
    {
        var python = ResolveProjectPython(workingDirectory);
        if (python is null) return null;

        string contents;
        try { contents = File.ReadAllText(filePath); }
        catch (IOException) { return null; }
        if (!contents.Contains("argparse")) return null;

        ArgparseResult? result;
        try { result = RunHelper(python, filePath); }
        catch { return null; }
        if (result is null) return null;
        if (result.Arguments.Count == 0 && string.IsNullOrWhiteSpace(result.Description)) return null;

        var (args, parameters, skipped) = BuildInvocation(filePath, result.Arguments);
        var description = !string.IsNullOrWhiteSpace(result.Description)
            ? result.Description!
            : $"Run `{Path.GetFileName(filePath)}`.";
        if (skipped.Count > 0)
        {
            description = $"{description} (boolean flags: {string.Join(", ", skipped)} — edit args to enable)";
        }

        return new TaskCandidate
        {
            Source = $"py:argparse:{Path.GetFileName(filePath)}",
            SuggestedName = TaskCandidate.Sanitize($"py_{Path.GetFileNameWithoutExtension(filePath)}"),
            Description = description,
            Command = python,
            Args = args,
            WorkingDirectory = workingDirectory,
            Parameters = parameters,
            SourceText = TruncateForLlm(contents)
        };
    }

    private static string TruncateForLlm(string text, int max = 2500)
    {
        if (text.Length <= max) return text;
        return text[..max] + "\n... (truncated)";
    }

    private static (List<string> args, List<TaskParameter> parameters, List<string> skipped) BuildInvocation(
        string scriptPath, IReadOnlyList<ArgparseArg> arguments)
    {
        var args = new List<string> { scriptPath };
        var parameters = new List<TaskParameter>();
        var skipped = new List<string>();

        foreach (var a in arguments.Where(a => a.IsPositional))
        {
            args.Add($"{{{a.Name}}}");
            parameters.Add(ToTaskParameter(a));
        }

        foreach (var a in arguments.Where(a => !a.IsPositional && !a.IsBooleanFlag))
        {
            var flag = a.LongFlag ?? a.ShortFlag;
            if (flag is null) continue;
            args.Add(flag);
            args.Add($"{{{a.Name}}}");
            parameters.Add(ToTaskParameter(a));
        }

        foreach (var a in arguments.Where(a => a.IsBooleanFlag))
        {
            var flag = a.LongFlag ?? a.ShortFlag;
            if (flag is not null) skipped.Add(flag);
        }

        return (args, parameters, skipped);
    }

    private static TaskParameter ToTaskParameter(ArgparseArg a) => new()
    {
        Name = a.Name,
        Type = a.Type,
        Required = a.Required,
        Default = a.Default,
        Description = string.IsNullOrWhiteSpace(a.Help) ? $"argparse arg '{a.Name}'" : a.Help,
        Enum = a.Choices
    };

    private static string? ResolvePython()
    {
        foreach (var name in PythonCandidates)
        {
            var psi = new ProcessStartInfo
            {
                FileName = name,
                Arguments = "-c \"import sys; sys.exit(0)\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return name;
            }
            catch { }
        }
        return null;
    }

    private static ArgparseResult? RunHelper(string python, string targetFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(HelperScript);
        psi.ArgumentList.Add(targetFile);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        if (proc.ExitCode != 0) return null;

        var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) return null;
        if (!root.TryGetProperty("arguments", out var argsEl)) return null;

        var result = new ArgparseResult
        {
            Description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null
        };
        foreach (var el in argsEl.EnumerateArray())
        {
            result.Arguments.Add(ParseArg(el));
        }
        return result;
    }

    private static ArgparseArg ParseArg(JsonElement el)
    {
        var arg = new ArgparseArg
        {
            Name = el.GetProperty("name").GetString() ?? "arg",
            Type = el.GetProperty("type").GetString() ?? "string",
            Required = el.GetProperty("required").GetBoolean(),
            IsPositional = el.GetProperty("is_positional").GetBoolean(),
            IsBooleanFlag = el.GetProperty("is_boolean_flag").GetBoolean(),
            LongFlag = el.TryGetProperty("long_flag", out var lf) && lf.ValueKind == JsonValueKind.String
                ? lf.GetString() : null,
            ShortFlag = el.TryGetProperty("short_flag", out var sf) && sf.ValueKind == JsonValueKind.String
                ? sf.GetString() : null,
            Help = el.TryGetProperty("help", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() : null
        };
        if (el.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null)
        {
            arg.Default = def.ValueKind switch
            {
                JsonValueKind.String => def.GetString(),
                JsonValueKind.Number when def.TryGetInt64(out var l) => (object)l,
                JsonValueKind.Number => def.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => def.ToString()
            };
        }
        if (el.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            arg.Choices = ch.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }
        return arg;
    }

    private sealed class ArgparseResult
    {
        public string? Description { get; set; }
        public List<ArgparseArg> Arguments { get; } = new();
    }

    private sealed class ArgparseArg
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "string";
        public bool Required { get; set; }
        public object? Default { get; set; }
        public string? Help { get; set; }
        public List<string>? Choices { get; set; }
        public bool IsPositional { get; set; }
        public bool IsBooleanFlag { get; set; }
        public string? LongFlag { get; set; }
        public string? ShortFlag { get; set; }
    }
}
