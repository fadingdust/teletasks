using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

public sealed class VsCodeTasksDetectorTests : IDisposable
{
    private readonly string _parent;
    private readonly string _root;

    public VsCodeTasksDetectorTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "teletasks-vsc-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_parent, "proj");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".vscode"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { }
    }

    private void WriteVsCodeTasks(string contents)
    {
        File.WriteAllText(Path.Combine(_root, ".vscode", "tasks.json"), contents);
    }

    [Fact]
    public void Detect_emits_one_candidate_per_task_with_a_command()
    {
        WriteVsCodeTasks("""
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "build", "command": "tsc",   "args": ["-p", "."] },
                { "label": "test",  "command": "vitest", "args": ["run"] }
              ]
            }
            """);

        var candidates = VsCodeTasksDetector.Detect(_root).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(new[] { "vsc_proj_build", "vsc_proj_test" }, candidates.Select(c => c.SuggestedName).ToArray());
    }

    [Fact]
    public void Detect_uses_detail_as_description_when_present()
    {
        WriteVsCodeTasks("""
            {
              "tasks": [
                { "label": "build", "command": "tsc", "detail": "Compile TypeScript sources" }
              ]
            }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Equal("Compile TypeScript sources", c.Description);
    }

    [Fact]
    public void Detect_falls_back_to_synthetic_description_when_no_detail()
    {
        WriteVsCodeTasks("""
            {
              "tasks": [
                { "label": "deploy", "command": "rsync" }
              ]
            }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Contains("VS Code task", c.Description);
        Assert.Contains("deploy", c.Description);
    }

    [Fact]
    public void Detect_extracts_string_args()
    {
        WriteVsCodeTasks("""
            {
              "tasks": [
                { "label": "x", "command": "make", "args": ["build", "VERBOSE=1"] }
              ]
            }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Equal("make", c.Command);
        Assert.Equal(new[] { "build", "VERBOSE=1" }, c.Args.ToArray());
    }

    [Fact]
    public void Detect_extracts_object_form_args_using_the_value_field()
    {
        // VS Code lets args be {"value": "...", "quoting": ...} objects.
        WriteVsCodeTasks("""
            {
              "tasks": [
                {
                  "label": "x",
                  "command": "make",
                  "args": [
                    { "value": "build", "quoting": "strong" },
                    { "value": "--silent" }
                  ]
                }
              ]
            }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Equal(new[] { "build", "--silent" }, c.Args.ToArray());
    }

    [Fact]
    public void Detect_skips_tasks_without_a_command()
    {
        // VS Code groups (compound tasks, dependsOn-only entries) lack
        // a command — we don't synthesise one; we just skip.
        WriteVsCodeTasks("""
            {
              "tasks": [
                { "label": "build-all", "dependsOn": ["build", "test"] },
                { "label": "build", "command": "tsc" }
              ]
            }
            """);

        var names = VsCodeTasksDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "vsc_proj_build" }, names);
    }

    [Fact]
    public void Detect_tolerates_jsonc_comments_and_trailing_commas()
    {
        // VS Code's tasks.json is JSON-with-comments by spec; the detector
        // configures JsonDocumentOptions accordingly.
        WriteVsCodeTasks("""
            {
              // top-level config
              "version": "2.0.0",
              "tasks": [
                /* the build task */
                { "label": "build", "command": "tsc", },   // trailing comma in object
              ],
            }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Equal("vsc_proj_build", c.SuggestedName);
    }

    [Fact]
    public void Detect_returns_nothing_when_tasks_array_is_missing()
    {
        WriteVsCodeTasks("""
            { "version": "2.0.0" }
            """);

        Assert.Empty(VsCodeTasksDetector.Detect(_root));
    }

    [Fact]
    public void Detect_returns_nothing_for_invalid_json_without_throwing()
    {
        WriteVsCodeTasks("{ this is not valid }");
        Assert.Empty(VsCodeTasksDetector.Detect(_root));
    }

    [Fact]
    public void Detect_returns_nothing_when_dotvscode_dir_absent()
    {
        Directory.Delete(Path.Combine(_root, ".vscode"), recursive: true);
        Assert.Empty(VsCodeTasksDetector.Detect(_root));
    }

    [Fact]
    public void Detect_source_includes_the_label_for_idempotent_merge()
    {
        WriteVsCodeTasks("""
            { "tasks": [ { "label": "ship", "command": "deploy.sh" } ] }
            """);

        var c = VsCodeTasksDetector.Detect(_root).Single();
        Assert.Equal(".vscode/tasks.json:proj:ship", c.Source);
    }
}
