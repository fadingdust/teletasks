using TeleTasks.Discovery.Detectors;
using Xunit;

namespace TeleTasks.Tests;

public sealed class PackageJsonDetectorTests : IDisposable
{
    private readonly string _parent;
    private readonly string _root;

    public PackageJsonDetectorTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "teletasks-pkg-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_parent, "proj");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { }
    }

    private void WritePackageJson(string contents)
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), contents);
    }

    [Fact]
    public void Detect_emits_one_candidate_per_script()
    {
        WritePackageJson("""
            {
              "name": "demo",
              "scripts": {
                "build": "tsc -p .",
                "test": "vitest run",
                "lint": "eslint ."
              }
            }
            """);

        var names = PackageJsonDetector.Detect(_root).Select(c => c.SuggestedName).ToArray();
        Assert.Equal(new[] { "npm_proj_build", "npm_proj_test", "npm_proj_lint" }, names);
    }

    [Fact]
    public void Detect_command_runs_npm_run_with_the_script_name()
    {
        WritePackageJson("""
            { "scripts": { "build": "tsc" } }
            """);

        var c = PackageJsonDetector.Detect(_root).Single();
        Assert.Equal("/usr/bin/env", c.Command);
        Assert.Equal(new[] { "npm", "run", "build" }, c.Args.ToArray());
        Assert.Equal(_root, c.WorkingDirectory);
    }

    [Fact]
    public void Detect_includes_the_script_body_in_the_description()
    {
        WritePackageJson("""
            { "scripts": { "test": "vitest run --reporter=verbose" } }
            """);

        var c = PackageJsonDetector.Detect(_root).Single();
        Assert.Contains("npm run test", c.Description);
        Assert.Contains("vitest run --reporter=verbose", c.Description);
    }

    [Fact]
    public void Detect_handles_missing_scripts_block_gracefully()
    {
        WritePackageJson("""
            { "name": "no-scripts-here" }
            """);

        Assert.Empty(PackageJsonDetector.Detect(_root));
    }

    [Fact]
    public void Detect_ignores_invalid_json_without_throwing()
    {
        WritePackageJson("{ this is broken JSON ");
        Assert.Empty(PackageJsonDetector.Detect(_root));
    }

    [Fact]
    public void Detect_returns_nothing_when_package_json_is_absent()
    {
        Assert.Empty(PackageJsonDetector.Detect(_root));
    }

    [Fact]
    public void Detect_source_field_is_packageJson_scriptName_for_merge()
    {
        WritePackageJson("""
            { "scripts": { "ship": "npm publish" } }
            """);

        var c = PackageJsonDetector.Detect(_root).Single();
        Assert.Equal("package.json:proj:ship", c.Source);
    }

    [Fact]
    public void Detect_skips_non_string_script_values()
    {
        // package.json scripts are always strings in practice; non-string
        // values yield an empty body but the entry should still be emitted
        // with the synthetic "Run `npm run X`." description.
        WritePackageJson("""
            { "scripts": { "weird": 42, "normal": "echo hi" } }
            """);

        var candidates = PackageJsonDetector.Detect(_root).ToList();
        var weird = candidates.Single(c => c.SuggestedName == "npm_proj_weird");
        Assert.Equal("Run `npm run weird`.", weird.Description);
    }
}
