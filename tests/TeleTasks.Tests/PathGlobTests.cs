using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

/// <summary>
/// PathGlob touches the filesystem, so each test creates an isolated tmp dir
/// and tears it down. xUnit's IDisposable contract handles the cleanup.
/// </summary>
public sealed class PathGlobTests : IDisposable
{
    private readonly string _root;

    public PathGlobTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ContainsGlob_detects_star_and_question_mark()
    {
        Assert.True(PathGlob.ContainsGlob("foo/*"));
        Assert.True(PathGlob.ContainsGlob("foo?bar"));
        Assert.False(PathGlob.ContainsGlob("foo/bar"));
        Assert.False(PathGlob.ContainsGlob(""));
    }

    [Fact]
    public void ResolveDirectory_returns_input_when_no_glob_and_dir_exists()
    {
        var dir = Path.Combine(_root, "concrete");
        Directory.CreateDirectory(dir);
        Assert.Equal(dir, PathGlob.ResolveDirectory(dir));
    }

    [Fact]
    public void ResolveDirectory_returns_null_for_non_glob_missing_dir()
    {
        Assert.Null(PathGlob.ResolveDirectory(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void ResolveDirectory_returns_null_when_glob_matches_nothing()
    {
        // No subdirs at all under root.
        Assert.Null(PathGlob.ResolveDirectory(Path.Combine(_root, "*")));
    }

    [Fact]
    public void ResolveDirectory_picks_freshest_match_by_mtime()
    {
        // Create three subdirs with deliberately staggered mtimes.
        var older = Path.Combine(_root, "lora-a");
        var middle = Path.Combine(_root, "lora-b");
        var newest = Path.Combine(_root, "lora-c");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(middle);
        Directory.CreateDirectory(newest);
        Directory.SetLastWriteTimeUtc(older,  DateTime.UtcNow.AddMinutes(-30));
        Directory.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddMinutes(-15));
        Directory.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

        Assert.Equal(newest, PathGlob.ResolveDirectory(Path.Combine(_root, "*")));
    }

    [Fact]
    public void ResolveDirectory_expands_multi_segment_globs()
    {
        // results/<lora>/output pattern — the wildcard is in the middle segment.
        var hit = Path.Combine(_root, "lora-foo", "output");
        Directory.CreateDirectory(hit);
        var miss = Path.Combine(_root, "lora-bar", "logs");
        Directory.CreateDirectory(miss);

        Assert.Equal(hit, PathGlob.ResolveDirectory(Path.Combine(_root, "*", "output")));
    }

    [Fact]
    public void ResolveFile_returns_freshest_file_match()
    {
        var older = Path.Combine(_root, "a.png");
        var newer = Path.Combine(_root, "b.png");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        Assert.Equal(newer, PathGlob.ResolveFile(Path.Combine(_root, "*.png")));
    }

    [Fact]
    public void ResolveFile_returns_null_for_directory_match_only()
    {
        // Pattern matches a directory but ResolveFile only accepts files.
        var dir = Path.Combine(_root, "bucket");
        Directory.CreateDirectory(dir);
        Assert.Null(PathGlob.ResolveFile(Path.Combine(_root, "*")));
    }

    [Fact]
    public void ResolveDirectory_handles_question_mark_wildcard()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a1"));
        Directory.CreateDirectory(Path.Combine(_root, "ab"));
        Directory.CreateDirectory(Path.Combine(_root, "abc"));

        // ? matches a single char, so "a?" hits a1 + ab but not abc.
        var result = PathGlob.ResolveDirectory(Path.Combine(_root, "a?"));
        Assert.NotNull(result);
        Assert.Contains(Path.GetFileName(result!), new[] { "a1", "ab" });
    }
}
