using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

public sealed class MakefileDetectorTests : IDisposable
{
    private readonly string _root;

    public MakefileDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-make-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteMakefile(string contents, string name = "Makefile")
    {
        File.WriteAllText(Path.Combine(_root, name), contents);
    }

    [Fact]
    public void Detect_emits_one_candidate_per_target()
    {
        WriteMakefile("""
            build:
            	echo build

            test:
            	echo test

            clean:
            	rm -rf out
            """.Replace("    ", ""));   // strip 4-space indent so tabs are tabs

        var candidates = MakefileDetector.Detect(_root).ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Equal(new[] { "make_build", "make_test", "make_clean" },
                     candidates.Select(c => c.SuggestedName).ToArray());
    }

    [Fact]
    public void Detect_uses_the_preceding_comment_as_description()
    {
        // tab-indented recipe lines are required by Make; we encode them explicitly.
        var contents =
            "# Build the binary\n" +
            "build:\n" +
            "\techo building\n";
        WriteMakefile(contents);

        var c = MakefileDetector.Detect(_root).Single();
        Assert.Equal("Build the binary", c.Description);
    }

    [Fact]
    public void Detect_falls_back_to_a_synthetic_description_when_no_comment()
    {
        WriteMakefile("build:\n\techo b\n");
        var c = MakefileDetector.Detect(_root).Single();
        Assert.Contains("make build", c.Description);
        Assert.Contains("Makefile", c.Description);
    }

    [Fact]
    public void Detect_command_is_make_with_C_flag_and_target_name()
    {
        WriteMakefile("build:\n\techo b\n");
        var c = MakefileDetector.Detect(_root).Single();

        Assert.Equal("/usr/bin/make", c.Command);
        Assert.Equal(new[] { "-C", _root, "build" }, c.Args.ToArray());
    }

    [Fact]
    public void Detect_source_is_Makefile_target_for_idempotent_merge()
    {
        WriteMakefile("deploy:\n\techo d\n");
        var c = MakefileDetector.Detect(_root).Single();
        Assert.Equal("Makefile:deploy", c.Source);
    }

    [Fact]
    public void Detect_skips_lines_that_look_like_variable_assignments()
    {
        // FOO := bar — the regex's negative-lookahead `(?!=)` rejects `:=` so
        // FOO doesn't get treated as a target.
        WriteMakefile(
            "FOO := bar\n" +
            "build:\n\techo b\n");
        var candidates = MakefileDetector.Detect(_root).ToList();
        Assert.Single(candidates);
        Assert.Equal("make_build", candidates[0].SuggestedName);
    }

    [Fact]
    public void Detect_skips_dotted_targets()
    {
        // .PHONY, .DEFAULT, etc. are pseudo-targets, not user-runnable.
        WriteMakefile(
            ".PHONY: build\n" +
            "build:\n\techo b\n");
        var candidates = MakefileDetector.Detect(_root).ToList();
        Assert.Single(candidates);
        Assert.Equal("make_build", candidates[0].SuggestedName);
    }

    [Fact]
    public void Detect_skips_tab_indented_recipe_lines_that_resemble_targets()
    {
        // A line inside a recipe that happens to start with `name:` shouldn't
        // be lifted out as a target (tab-prefixed lines are skipped).
        WriteMakefile(
            "build:\n" +
            "\techo not_a_target: nothing\n" +
            "test:\n\techo t\n");
        var candidates = MakefileDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "make_build", "make_test" }, candidates);
    }

    [Fact]
    public void Detect_handles_GNUmakefile_filename()
    {
        WriteMakefile("build:\n\techo b\n", name: "GNUmakefile");
        var c = MakefileDetector.Detect(_root).Single();
        Assert.Equal("make_build", c.SuggestedName);
        // Description references the actual filename used.
        Assert.Contains("GNUmakefile", c.Description);
    }

    [Fact]
    public void Detect_returns_nothing_when_no_makefile_exists()
    {
        Assert.Empty(MakefileDetector.Detect(_root));
    }

    [Fact]
    public void Detect_captures_the_recipe_body_in_SourceText()
    {
        // SourceText feeds the LLM polish pass and the wrapper-resolver style
        // patterns. It should include the tab-indented recipe lines.
        WriteMakefile(
            "deploy:\n" +
            "\trsync -av out/ remote:/var/www/\n" +
            "\tssh remote systemctl restart nginx\n");
        var c = MakefileDetector.Detect(_root).Single();

        Assert.Contains("deploy:", c.SourceText);
        Assert.Contains("rsync", c.SourceText);
        Assert.Contains("systemctl restart", c.SourceText);
    }
}
