using TeleTasks.Discovery;
using TeleTasks.Models;
using Xunit;

namespace TeleTasks.Tests;

/// <summary>
/// Lazy deep-scan tests need a real Python file on disk because
/// ArgparsePythonDetector.DetectFromFile shells out to python3. The
/// in-memory paths skip that.
/// </summary>
public sealed class ShellWrapperResolverTests : IDisposable
{
    private readonly string _root;

    public ShellWrapperResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-wrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static TaskCandidate Sh(string sourceText, string name = "sh_run", string workingDir = "/tmp") => new()
    {
        Source = "sh:run.sh",
        SuggestedName = name,
        Description = "test sh",
        Command = "/bin/bash",
        SourceText = sourceText,
        WorkingDirectory = workingDir
    };

    private static TaskCandidate Py(string fileName = "render.py", TaskOutputType outputType = TaskOutputType.Images)
    {
        var c = new TaskCandidate
        {
            Source = $"py:argparse:{fileName}",
            SuggestedName = "py_" + Path.GetFileNameWithoutExtension(fileName),
            Description = "test py",
            Command = "python3",
            WorkingDirectory = "/tmp"
        };
        c.Output = outputType == TaskOutputType.Text
            ? new TaskOutputSpec { Type = TaskOutputType.Text }
            : new TaskOutputSpec
            {
                Type = TaskOutputType.Images,
                Directory = "{output_dir}",
                SortBy = "newest",
                CaptionFrom = new CaptionFromSpec { Sidecar = ".json", Mode = "auto-diff" }
            };
        c.Parameters.Add(new TaskParameter
        {
            Name = "output_dir",
            Type = "string",
            Default = "/tmp/renders"
        });
        return c;
    }

    [Fact]
    public void Resolve_copies_python_output_spec_onto_matching_sh_wrapper()
    {
        var sh = Sh("#!/bin/bash\npython3 render.py --prompt $1");
        var py = Py("render.py");

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Equal(TaskOutputType.Images, sh.Output.Type);
        Assert.Equal("{output_dir}", sh.Output.Directory);
        Assert.NotNull(sh.Output.CaptionFrom);
        Assert.Equal(".json", sh.Output.CaptionFrom!.Sidecar);
    }

    [Fact]
    public void Resolve_copies_templated_parameters_onto_the_shell()
    {
        // The python's output spec templates against {output_dir}; the wrapper
        // needs to declare output_dir as a parameter too so substitution works
        // when the bot runs the sh entry.
        var sh = Sh("#!/bin/bash\npython3 render.py");
        var py = Py("render.py");

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Contains(sh.Parameters, p => p.Name == "output_dir");
        var copied = sh.Parameters.Single(p => p.Name == "output_dir");
        Assert.Equal("/tmp/renders", copied.Default);
        Assert.Contains("inherited", copied.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_appends_wraps_note_to_description()
    {
        var sh = Sh("#!/bin/bash\npython3 app.py");
        var py = Py("app.py");

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Contains("wraps", sh.Description);
        Assert.Contains(py.SuggestedName, sh.Description);
    }

    [Theory]
    [InlineData("python3 app.py")]
    [InlineData("python -u app.py")]
    [InlineData("pipenv run python app.py")]
    [InlineData("poetry run python app.py")]
    [InlineData("uv run app.py")]
    [InlineData("$PYTHON app.py")]
    [InlineData("./app.py")]
    [InlineData("exec app.py")]
    public void Resolve_recognises_common_python_invocation_styles(string invocation)
    {
        var sh = Sh($"#!/bin/bash\nset -e\n{invocation}\n");
        var py = Py("app.py");

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Equal(TaskOutputType.Images, sh.Output.Type);
    }

    [Fact]
    public void Resolve_does_nothing_when_python_target_is_still_text_output()
    {
        // Promoter didn't fire on the python (e.g. no output-shaped param).
        // We don't propagate Text → Text noise.
        var sh = Sh("#!/bin/bash\npython3 app.py");
        var py = Py("app.py", TaskOutputType.Text);

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Equal(TaskOutputType.Text, sh.Output.Type);
    }

    [Fact]
    public void Resolve_does_not_clobber_existing_non_text_output_on_shell()
    {
        // Hand-edited shell with its own output spec. Don't overwrite.
        var sh = Sh("#!/bin/bash\npython3 app.py");
        sh.Output = new TaskOutputSpec { Type = TaskOutputType.File, Path = "/already/set" };
        var py = Py("app.py");

        ShellWrapperResolver.Resolve(new[] { sh, py });

        Assert.Equal(TaskOutputType.File, sh.Output.Type);
        Assert.Equal("/already/set", sh.Output.Path);
    }

    [Fact]
    public void Resolve_logs_when_no_python_token_is_found()
    {
        var sh = Sh("#!/bin/bash\nset -e\necho hello\n");
        var py = Py("app.py");
        var lines = new List<string>();

        ShellWrapperResolver.Resolve(new[] { sh, py }, lines.Add);

        Assert.Contains(lines, l => l.Contains("no *.py tokens"));
    }

    [Fact]
    public void Resolve_logs_when_token_does_not_match_any_known_candidate()
    {
        var sh = Sh("#!/bin/bash\npython3 unknown.py");
        var py = Py("app.py");
        var lines = new List<string>();

        ShellWrapperResolver.Resolve(new[] { sh, py }, lines.Add);

        Assert.Contains(lines, l => l.Contains("but none matched") || l.Contains("none resolved"));
    }

    [Fact]
    public void Resolve_skips_candidates_with_empty_source_text()
    {
        var sh = new TaskCandidate
        {
            Source = "sh:empty.sh",
            SuggestedName = "sh_empty",
            Description = "",
            Command = "/bin/bash",
            SourceText = null,           // no source — wrapper resolver bails
            WorkingDirectory = "/tmp"
        };
        var py = Py("app.py");
        var lines = new List<string>();

        ShellWrapperResolver.Resolve(new[] { sh, py }, lines.Add);

        Assert.Equal(TaskOutputType.Text, sh.Output.Type);
        Assert.Contains(lines, l => l.Contains("no source text"));
    }

    [Fact]
    public void Resolve_does_not_treat_a_python_candidate_as_a_shell_wrapper()
    {
        // Sanity: only sh-sourced candidates are wrapper-eligible. A py-sourced
        // candidate must not get its own output overwritten by itself.
        var py1 = Py("a.py");
        var py2 = Py("b.py");
        var snapshotA = py1.Output.Type;

        ShellWrapperResolver.Resolve(new[] { py1, py2 });

        Assert.Equal(snapshotA, py1.Output.Type);
    }
}
