using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

public sealed class PyprojectDetectorTests : IDisposable
{
    private readonly string _root;

    public PyprojectDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-pyproj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WritePyproject(string contents)
    {
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), contents);
    }

    [Fact]
    public void Detect_picks_up_entries_under_project_scripts()
    {
        WritePyproject("""
            [project]
            name = "demo"
            version = "0.1.0"

            [project.scripts]
            mycli = "demo.cli:main"
            other = "demo.other:run"
            """);

        var candidates = PyprojectDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "py_mycli", "py_other" }, candidates);
    }

    [Fact]
    public void Detect_picks_up_entries_under_tool_poetry_scripts()
    {
        WritePyproject("""
            [tool.poetry]
            name = "demo"

            [tool.poetry.scripts]
            mytool = "demo.tool:main"
            """);

        var c = PyprojectDetector.Detect(_root).Single();
        Assert.Equal("py_mytool", c.SuggestedName);
    }

    [Fact]
    public void Detect_command_uses_env_with_the_console_script_name()
    {
        WritePyproject("""
            [project.scripts]
            mycli = "demo.cli:main"
            """);

        var c = PyprojectDetector.Detect(_root).Single();
        Assert.Equal("/usr/bin/env", c.Command);
        Assert.Equal(new[] { "mycli" }, c.Args.ToArray());
    }

    [Fact]
    public void Detect_description_mentions_the_entry_point()
    {
        WritePyproject("""
            [project.scripts]
            mycli = "demo.cli:main"
            """);

        var c = PyprojectDetector.Detect(_root).Single();
        Assert.Contains("mycli", c.Description);
        Assert.Contains("demo.cli:main", c.Description);
    }

    [Fact]
    public void Detect_source_includes_the_section_name_to_distinguish_categories()
    {
        // project.scripts.foo and tool.poetry.scripts.foo would otherwise
        // collide on source. Section is part of the source string.
        WritePyproject("""
            [project.scripts]
            foo = "demo:main"
            """);

        var c = PyprojectDetector.Detect(_root).Single();
        Assert.Equal("pyproject.toml:project.scripts.foo", c.Source);
    }

    [Fact]
    public void Detect_ignores_other_sections()
    {
        WritePyproject("""
            [project]
            name = "demo"

            [build-system]
            requires = ["setuptools"]

            [tool.black]
            line-length = 100
            """);

        Assert.Empty(PyprojectDetector.Detect(_root));
    }

    [Fact]
    public void Detect_skips_comment_lines()
    {
        WritePyproject("""
            [project.scripts]
            # this is the entry point
            mycli = "demo.cli:main"
            # commented_out = "demo.other:run"
            """);

        var candidates = PyprojectDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "py_mycli" }, candidates);
    }

    [Fact]
    public void Detect_returns_nothing_when_pyproject_toml_absent()
    {
        Assert.Empty(PyprojectDetector.Detect(_root));
    }

    [Fact]
    public void Detect_handles_both_sections_in_the_same_file()
    {
        WritePyproject("""
            [project.scripts]
            a = "pkg:a"

            [tool.poetry.scripts]
            b = "pkg:b"
            """);

        var candidates = PyprojectDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Contains("py_a", candidates);
        Assert.Contains("py_b", candidates);
    }
}
