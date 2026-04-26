using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

/// <summary>
/// ArgparsePythonDetector spawns python3 to AST-walk the script. Each fact
/// guards on PythonAvailable.Value so a host without Python sees the tests
/// skipped rather than failing.
/// </summary>
public sealed class ArgparsePythonDetectorTests : IDisposable
{
    private readonly string _root;

    public ArgparsePythonDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-pyargs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WritePy(string name, string source)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static void RequirePython()
    {
        Skip.IfNot(PythonAvailable.Value, "python3 is not available on PATH");
    }

    [SkippableFact]
    public void Detect_extracts_required_string_argument()
    {
        RequirePython();
        WritePy("render.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--prompt', required=True, help='What to render')
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        Assert.Equal("py_render", c.SuggestedName);
        var prompt = c.Parameters.Single(x => x.Name == "prompt");
        Assert.Equal("string", prompt.Type);
        Assert.True(prompt.Required);
        Assert.Equal("What to render", prompt.Description);
    }

    [SkippableFact]
    public void Detect_recognises_typed_arguments()
    {
        RequirePython();
        WritePy("train.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--epochs', type=int, default=10)
            p.add_argument('--lr',     type=float, default=0.001)
            p.add_argument('--name',   type=str)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        var byName = c.Parameters.ToDictionary(p => p.Name);

        Assert.Equal("integer", byName["epochs"].Type);
        Assert.Equal("number",  byName["lr"].Type);
        Assert.Equal("string",  byName["name"].Type);
    }

    [SkippableFact]
    public void Detect_picks_up_default_values_and_flips_required_to_false()
    {
        RequirePython();
        WritePy("greet.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--name', default='world')
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        var name = c.Parameters.Single(x => x.Name == "name");
        Assert.False(name.Required);
        Assert.Equal("world", name.Default);
    }

    [SkippableFact]
    public void Detect_recognises_choices_as_enum()
    {
        RequirePython();
        WritePy("env.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--env', choices=['dev', 'staging', 'prod'], required=True)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        var env = c.Parameters.Single(x => x.Name == "env");
        Assert.NotNull(env.Enum);
        Assert.Equal(new[] { "dev", "staging", "prod" }, env.Enum!.ToArray());
    }

    [SkippableFact]
    public void Detect_separates_boolean_flags_from_normal_parameters()
    {
        RequirePython();
        // store_true flags don't take a value; the bot can't conditionally
        // emit them without a template syntax we haven't built. They get
        // listed in the description rather than added as Parameters.
        WritePy("flags.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--verbose', action='store_true')
            p.add_argument('--prompt', required=True)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        Assert.DoesNotContain(c.Parameters, x => x.Name == "verbose");
        Assert.Contains(c.Parameters, x => x.Name == "prompt");
        Assert.Contains("--verbose", c.Description);
    }

    [SkippableFact]
    public void Detect_uses_parser_description_as_task_description()
    {
        RequirePython();
        WritePy("hello.py", """
            import argparse
            p = argparse.ArgumentParser(description='Render an image from a prompt.')
            p.add_argument('--prompt', required=True)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        Assert.Contains("Render an image from a prompt.", c.Description);
    }

    [SkippableFact]
    public void Detect_args_layout_starts_with_script_path_then_named_flags()
    {
        RequirePython();
        WritePy("run.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--prompt', required=True)
            p.add_argument('--steps', type=int, default=30)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.Detect(_root).Single();
        // Script path comes first, then `--flag {name}` pairs.
        Assert.EndsWith("run.py", c.Args[0]);
        Assert.Contains("--prompt", c.Args);
        Assert.Contains("{prompt}", c.Args);
        Assert.Contains("--steps", c.Args);
        Assert.Contains("{steps}", c.Args);
    }

    [SkippableFact]
    public void Detect_skips_files_that_do_not_import_argparse()
    {
        RequirePython();
        WritePy("plain.py", "print('hello')\n");
        Assert.Empty(ArgparsePythonDetector.Detect(_root));
    }

    [SkippableFact]
    public void Detect_only_walks_top_level_python_files()
    {
        RequirePython();
        // The detector floors discovery at top-level; nested helpers stay
        // below the floor (and only get a wrapper-resolver lazy-scan).
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(_root, "scripts", "helper.py"), """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--prompt', required=True)
            args = p.parse_args()
            """);
        WritePy("entry.py", """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--name')
            args = p.parse_args()
            """);

        var names = ArgparsePythonDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "py_entry" }, names);
    }

    [SkippableFact]
    public void DetectFromFile_works_on_a_subdirectory_python_file()
    {
        RequirePython();
        // Used by ShellWrapperResolver's lazy deep-scan when a sh wrapper
        // calls `python scripts/foo.py`.
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        var path = Path.Combine(_root, "scripts", "deep.py");
        File.WriteAllText(path, """
            import argparse
            p = argparse.ArgumentParser()
            p.add_argument('--prompt', required=True)
            args = p.parse_args()
            """);

        var c = ArgparsePythonDetector.DetectFromFile(path, _root);
        Assert.NotNull(c);
        Assert.Equal("py_deep", c!.SuggestedName);
        Assert.Contains(c.Parameters, p => p.Name == "prompt");
    }

    [SkippableFact]
    public void DetectFromFile_returns_null_when_file_has_no_argparse()
    {
        RequirePython();
        var path = Path.Combine(_root, "plain.py");
        File.WriteAllText(path, "print('hi')\n");
        Assert.Null(ArgparsePythonDetector.DetectFromFile(path, _root));
    }
}
