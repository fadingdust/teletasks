using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

public sealed class JustfileDetectorTests : IDisposable
{
    private readonly string _parent;
    private readonly string _root;

    public JustfileDetectorTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "teletasks-just-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_parent, "proj");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { }
    }

    private void WriteJustfile(string contents, string name = "justfile")
    {
        File.WriteAllText(Path.Combine(_root, name), contents);
    }

    [Fact]
    public void Detect_emits_one_candidate_per_recipe()
    {
        WriteJustfile("""
            build:
                cargo build

            test:
                cargo test
            """);

        var candidates = JustfileDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "just_proj_build", "just_proj_test" }, candidates);
    }

    [Fact]
    public void Detect_picks_up_a_preceding_comment_as_description()
    {
        WriteJustfile("""
            # Build the release binary
            build:
                cargo build --release
            """);

        var c = JustfileDetector.Detect(_root).Single();
        Assert.Equal("Build the release binary", c.Description);
    }

    [Fact]
    public void Detect_falls_back_to_synthetic_description_when_no_comment()
    {
        WriteJustfile("hello:\n    echo hi\n");
        var c = JustfileDetector.Detect(_root).Single();
        Assert.Contains("just hello", c.Description);
    }

    [Fact]
    public void Detect_extracts_required_recipe_parameters()
    {
        WriteJustfile("""
            deploy env:
                ./deploy.sh {{env}}
            """);

        var c = JustfileDetector.Detect(_root).Single();
        Assert.Single(c.Parameters);
        Assert.Equal("env", c.Parameters[0].Name);
        Assert.True(c.Parameters[0].Required);
        Assert.Null(c.Parameters[0].Default);
    }

    [Fact]
    public void Detect_extracts_parameters_with_defaults()
    {
        // Just lets you write `recipe arg='default':` for an optional arg.
        WriteJustfile("""
            greet name='world':
                echo hello {{name}}
            """);

        var c = JustfileDetector.Detect(_root).Single();
        Assert.Single(c.Parameters);
        var p = c.Parameters[0];
        Assert.Equal("name", p.Name);
        Assert.False(p.Required);
        Assert.Equal("world", p.Default);
    }

    [Fact]
    public void Detect_args_include_just_invocation_then_recipe_name_then_param_placeholders()
    {
        WriteJustfile("""
            deploy env target:
                echo {{env}} {{target}}
            """);

        var c = JustfileDetector.Detect(_root).Single();
        // Args layout: [just, --justfile, <path>, --working-directory, <root>, recipe, {param1}, {param2}]
        Assert.Equal("/usr/bin/env", c.Command);
        Assert.Contains("just", c.Args);
        Assert.Contains("--justfile", c.Args);
        Assert.Contains("--working-directory", c.Args);
        Assert.Contains("deploy", c.Args);
        Assert.Contains("{env}", c.Args);
        Assert.Contains("{target}", c.Args);
        // Justfile path is correctly threaded through.
        Assert.Contains(c.Args, a => a.EndsWith("justfile"));
    }

    [Fact]
    public void Detect_skips_underscore_prefixed_recipes()
    {
        // _private recipes are just convention for "internal helper, don't show".
        WriteJustfile("""
            _setup:
                echo internal

            build:
                echo b
            """);

        var names = JustfileDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "just_proj_build" }, names);
    }

    [Fact]
    public void Detect_skips_assignment_lines_that_use_colon_equals()
    {
        // `version := "1.0.0"` is a variable, not a recipe header.
        WriteJustfile("""
            version := "1.0.0"

            build:
                echo {{version}}
            """);

        var names = JustfileDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "just_proj_build" }, names);
    }

    [Fact]
    public void Detect_source_uses_recipe_name_for_idempotent_merge()
    {
        WriteJustfile("ship:\n    echo s\n");
        var c = JustfileDetector.Detect(_root).Single();
        Assert.Equal("justfile:proj:ship", c.Source);
    }

    [Fact]
    public void Detect_handles_alternate_filename_capitalisations()
    {
        WriteJustfile("build:\n    echo b\n", name: "Justfile");
        var c = JustfileDetector.Detect(_root).Single();
        Assert.Equal("just_proj_build", c.SuggestedName);
    }

    [Fact]
    public void Detect_returns_nothing_when_no_justfile_exists()
    {
        Assert.Empty(JustfileDetector.Detect(_root));
    }

    [Fact]
    public void Detect_captures_the_recipe_body_in_SourceText()
    {
        WriteJustfile("""
            ship:
                cargo build --release
                rsync -av target/release/app remote:/usr/local/bin/
            """);

        var c = JustfileDetector.Detect(_root).Single();
        Assert.Contains("cargo build", c.SourceText);
        Assert.Contains("rsync", c.SourceText);
    }
}
