using TeleTasks.Discovery.Detectors;
using TeleTasks.Models;
using Xunit;

namespace TeleTasks.Tests;

public sealed class ShellScriptDetectorTests : IDisposable
{
    private readonly string _root;

    public ShellScriptDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-shdetect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string name, string contents)
    {
        File.WriteAllText(Path.Combine(_root, name), contents);
    }

    [Fact]
    public void Detect_emits_one_candidate_per_sh_file_in_the_top_level()
    {
        Write("a.sh", "#!/bin/bash\necho a\n");
        Write("b.sh", "#!/bin/bash\necho b\n");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        Write("nested/skipped.sh", "#!/bin/bash\necho nope\n");

        var candidates = ShellScriptDetector.Detect(_root).ToList();

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.SuggestedName == "sh_a");
        Assert.Contains(candidates, c => c.SuggestedName == "sh_b");
        Assert.DoesNotContain(candidates, c => c.SuggestedName == "sh_skipped");
    }

    [Fact]
    public void Detect_uses_the_Description_header_as_the_task_description()
    {
        Write("greet.sh", """
            #!/bin/bash
            # Description: Say hello to the world
            echo hi
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        Assert.Equal("Say hello to the world", c.Description);
    }

    [Fact]
    public void Detect_falls_back_to_a_paragraph_of_leading_comments()
    {
        Write("explain.sh", """
            #!/bin/bash
            # Builds the project and runs tests.
            # Pass --fast to skip the integration suite.
            set -e
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        // Concatenated header comment, separated by spaces.
        Assert.Contains("Builds the project", c.Description);
        Assert.Contains("--fast", c.Description);
    }

    [Fact]
    public void Detect_extracts_bare_positional_args()
    {
        Write("run.sh", """
            #!/bin/bash
            echo "$1 / $2 / $3"
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        var positional = c.Parameters
            .Where(p => p.Name.StartsWith("arg"))
            .OrderBy(p => p.Name)
            .ToList();

        Assert.Equal(3, positional.Count);
        Assert.Equal(new[] { "arg1", "arg2", "arg3" }, positional.Select(p => p.Name));
        Assert.All(positional, p => Assert.True(p.Required));
        Assert.All(positional, p => Assert.Null(p.Default));
    }

    [Fact]
    public void Detect_picks_up_default_values_with_brace_syntax()
    {
        Write("run.sh", """
            #!/bin/bash
            target=${1:-prod}
            count=${2:-5}
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        var p1 = c.Parameters.Single(p => p.Name == "arg1");
        var p2 = c.Parameters.Single(p => p.Name == "arg2");

        Assert.False(p1.Required);
        Assert.Equal("prod", p1.Default);
        Assert.False(p2.Required);
        Assert.Equal("5", p2.Default);
    }

    [Fact]
    public void Detect_does_not_confuse_dollar_10_for_arg1()
    {
        // Bash positionals beyond $9 use ${10}; the bare-regex must NOT
        // greedily match $10 as $1 followed by '0'.
        Write("run.sh", """
            #!/bin/bash
            echo "${10}"
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        Assert.DoesNotContain(c.Parameters, p => p.Name.StartsWith("arg"));
    }

    [Fact]
    public void Detect_args_list_starts_with_script_path_then_positional_placeholders()
    {
        Write("invoke.sh", "#!/bin/bash\necho \"$1 $2\"\n");
        var c = ShellScriptDetector.Detect(_root).Single();

        // First arg = script path, then {arg1} / {arg2} as templated placeholders.
        Assert.Equal(3, c.Args.Count);
        Assert.EndsWith("invoke.sh", c.Args[0]);
        Assert.Equal("{arg1}", c.Args[1]);
        Assert.Equal("{arg2}", c.Args[2]);
    }

    [Fact]
    public void Detect_summarises_getopts_flags_in_the_description()
    {
        // Flag-style options (getopts / case) are not first-class Parameters
        // because we don't yet have a conditional-emit syntax for "include
        // -v in args only when the user said yes". They're surfaced in the
        // description as "(flags: -v, -f, -o — edit args to use)" so the
        // user can hand-wire them in tasks.json.
        Write("opts.sh", """
            #!/bin/bash
            while getopts "vf:o:" opt; do :; done
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        Assert.Empty(c.Parameters);
        Assert.Contains("flags:", c.Description);
        Assert.Contains("-v", c.Description);
        Assert.Contains("-f", c.Description);
        Assert.Contains("-o", c.Description);
        Assert.Contains("edit args to use", c.Description);
    }

    [Fact]
    public void Detect_summarises_case_flags_in_the_description()
    {
        // Common pattern: case "$1" in -h) ...; -v) ...; esac
        // These surface as a flag list in the description, not first-class
        // Parameters. The $1 in `case "$1"` itself does become an arg1
        // positional — that's expected and fine.
        Write("opts.sh", """
            #!/bin/bash
            case "$1" in
              -h) usage ;;
              -v) verbose=1 ;;
            esac
            """);

        var c = ShellScriptDetector.Detect(_root).Single();
        // No -h or -v in Parameters (would show up as h / v if they were).
        Assert.DoesNotContain(c.Parameters, p => p.Name == "h");
        Assert.DoesNotContain(c.Parameters, p => p.Name == "v");
        Assert.Contains("-h", c.Description);
        Assert.Contains("-v", c.Description);
    }

    [Fact]
    public void Detect_command_is_bin_bash_and_workingDirectory_is_project_root()
    {
        Write("script.sh", "#!/bin/bash\necho hi\n");
        var c = ShellScriptDetector.Detect(_root).Single();

        Assert.Equal("/bin/bash", c.Command);
        Assert.Equal(_root, c.WorkingDirectory);
    }

    [Fact]
    public void Detect_source_field_uses_basename_for_idempotent_merge()
    {
        Write("anything.sh", "#!/bin/bash\n");
        var c = ShellScriptDetector.Detect(_root).Single();
        Assert.Equal("sh:anything.sh", c.Source);
    }

    [Fact]
    public void Detect_skips_completely_empty_files()
    {
        Write("empty.sh", "");
        var candidates = ShellScriptDetector.Detect(_root).ToList();
        Assert.Empty(candidates);
    }

    [Fact]
    public void Detect_keeps_full_source_text_on_candidate()
    {
        // The 2 MB cap is a sanity bound, not a 2 KB truncation. Anything a
        // realistic shell script could be should round-trip verbatim.
        var body = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"# line {i}"));
        Write("long.sh", "#!/bin/bash\n" + body + "\necho done\n");

        var c = ShellScriptDetector.Detect(_root).Single();
        Assert.Contains("line 0", c.SourceText);
        Assert.Contains("line 199", c.SourceText);
        Assert.Contains("echo done", c.SourceText);
    }
}
