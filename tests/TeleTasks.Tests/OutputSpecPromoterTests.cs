using TeleTasks.Discovery;
using TeleTasks.Models;
using Xunit;

namespace TeleTasks.Tests;

/// <summary>
/// Classify is private, so we test it through the public Promote entry point —
/// asserting on the final Output spec, which is what callers care about anyway.
/// </summary>
public class OutputSpecPromoterTests
{
    private static TaskCandidate Candidate(params (string name, string type, object? def)[] parameters)
    {
        var c = new TaskCandidate
        {
            Source = "test:fixture",
            SuggestedName = "test_fixture",
            Description = "test",
            Command = "/bin/true",
            WorkingDirectory = null
        };
        foreach (var (name, type, def) in parameters)
        {
            c.Parameters.Add(new TaskParameter
            {
                Name = name,
                Type = type,
                Default = def
            });
        }
        return c;
    }

    [Fact]
    public void Promote_recognises_output_dir_as_Images()
    {
        var c = Candidate(("output_dir", "string", "/tmp/whatever"));
        OutputSpecPromoter.Promote(c);

        Assert.Equal(TaskOutputType.Images, c.Output.Type);
        Assert.Equal("{output_dir}", c.Output.Directory);
        Assert.Equal("newest", c.Output.SortBy);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("outputs")]
    [InlineData("results")]
    [InlineData("renders")]
    [InlineData("samples")]
    [InlineData("outdir")]
    public void Promote_recognises_bare_image_directory_names(string paramName)
    {
        var c = Candidate((paramName, "string", "/tmp"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Images, c.Output.Type);
    }

    [Theory]
    [InlineData("output_directory")]
    [InlineData("results_dir")]
    [InlineData("renders_folder")]
    [InlineData("checkpoints_dir")]
    public void Promote_recognises_compound_image_directory_names(string paramName)
    {
        var c = Candidate((paramName, "string", "/tmp"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Images, c.Output.Type);
    }

    [Fact]
    public void Promote_recognises_log_file_as_LogTail()
    {
        var c = Candidate(("log_file", "string", "/tmp/app.log"));
        OutputSpecPromoter.Promote(c);

        Assert.Equal(TaskOutputType.LogTail, c.Output.Type);
        Assert.Equal("{log_file}", c.Output.Path);
    }

    [Theory]
    [InlineData("logfile")]
    [InlineData("log_path")]
    [InlineData("logpath")]
    public void Promote_recognises_log_file_variants(string paramName)
    {
        var c = Candidate((paramName, "string", "/tmp/app.log"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.LogTail, c.Output.Type);
    }

    [Fact]
    public void Promote_does_not_treat_log_dir_as_LogTail_because_its_ambiguous()
    {
        // log_dir could be many log files; we don't promote bare log directories.
        var c = Candidate(("log_dir", "string", "/var/log"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Text, c.Output.Type);
    }

    [Fact]
    public void Promote_recognises_output_file_as_File()
    {
        var c = Candidate(("output_file", "string", "/tmp/out.json"));
        OutputSpecPromoter.Promote(c);

        Assert.Equal(TaskOutputType.File, c.Output.Type);
        Assert.Equal("{output_file}", c.Output.Path);
    }

    [Theory]
    [InlineData("output_path")]
    [InlineData("out_path")]
    [InlineData("dest_file")]
    [InlineData("destination_path")]
    public void Promote_recognises_output_file_variants(string paramName)
    {
        var c = Candidate((paramName, "string", "/tmp/out.json"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.File, c.Output.Type);
    }

    [Fact]
    public void Promote_priorities_Images_over_LogTail_when_both_present()
    {
        // "show me the latest renders" wins over "tail the log" — the log is
        // still reachable via /job N for long-running tasks.
        var c = Candidate(
            ("output_dir", "string", "/tmp/renders"),
            ("log_file",   "string", "/tmp/app.log"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Images, c.Output.Type);
    }

    [Fact]
    public void Promote_skips_when_two_params_match_the_same_kind()
    {
        // Ambiguous: which output dir? Don't guess.
        var c = Candidate(
            ("output_dir", "string", "/tmp/a"),
            ("results_dir", "string", "/tmp/b"));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Text, c.Output.Type);
    }

    [Fact]
    public void Promote_skips_when_no_param_is_strongly_named()
    {
        var c = Candidate(
            ("prompt", "string", null),
            ("seed",   "integer", 42));
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Text, c.Output.Type);
    }

    [Fact]
    public void Promote_skips_when_command_is_empty()
    {
        var c = new TaskCandidate
        {
            Source = "test",
            SuggestedName = "test",
            Description = "no command",
            Command = string.Empty
        };
        c.Parameters.Add(new TaskParameter { Name = "output_dir", Type = "string" });
        OutputSpecPromoter.Promote(c);
        Assert.Equal(TaskOutputType.Text, c.Output.Type);
    }

    [Fact]
    public void Promote_skips_when_output_already_promoted()
    {
        // Hand-edited tasks with non-Text output should be left alone.
        var c = Candidate(("output_dir", "string", "/tmp"));
        c.Output = new TaskOutputSpec { Type = TaskOutputType.File, Path = "/already/set" };
        OutputSpecPromoter.Promote(c);

        Assert.Equal(TaskOutputType.File, c.Output.Type);
        Assert.Equal("/already/set", c.Output.Path);
    }

    [Fact]
    public void Promote_logs_a_specific_skip_reason()
    {
        var c = Candidate(("seed", "integer", null));
        var lines = new List<string>();
        OutputSpecPromoter.Promote(c, lines.Add);

        Assert.Single(lines);
        // Reason should mention what params it saw so the user can tell whether
        // the param was just badly named vs. genuinely missing.
        Assert.Contains("skipped", lines[0]);
        Assert.Contains("seed", lines[0]);
    }
}
