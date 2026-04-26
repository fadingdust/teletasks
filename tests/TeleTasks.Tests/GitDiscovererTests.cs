using System.Diagnostics;
using TeleTasks.Discovery;
using TeleTasks.Models;
using Xunit;

namespace TeleTasks.Tests;

public sealed class GitDiscovererTests : IDisposable
{
    // The discoverer wants a real `.git` dir present (it actually invokes
    // git later, but discovery only checks for the presence of `.git`).
    private readonly string _parent;
    private readonly string _repo;

    public GitDiscovererTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "teletasks-git-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_parent, "demo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { }
    }

    [Fact]
    public void Discover_emits_pull_and_fetch_tasks_alongside_status_log_diff_branches()
    {
        var candidates = GitDiscoverer.Discover(_repo);
        var sources = candidates.Select(c => c.Source).ToList();

        Assert.Contains("git:demo:status",   sources);
        Assert.Contains("git:demo:log",      sources);
        Assert.Contains("git:demo:diff",     sources);
        Assert.Contains("git:demo:branches", sources);
        Assert.Contains("git:demo:fetch",    sources);
        Assert.Contains("git:demo:pull",     sources);
    }

    [Fact]
    public void Discover_pull_task_invokes_git_pull_with_minus_C_repo()
    {
        var pull = GitDiscoverer.Discover(_repo).Single(c => c.Source == "git:demo:pull");
        Assert.Equal("/usr/bin/git", pull.Command);
        Assert.Equal(new[] { "-C", _repo, "pull" }, pull.Args.ToArray());
        Assert.Equal(TaskOutputType.Text, pull.Output.Type);
        Assert.Equal("git_demo_pull", pull.SuggestedName);
    }

    [Fact]
    public void Discover_fetch_task_runs_fetch_all_and_prunes()
    {
        // --all picks up every remote; --prune removes refs deleted upstream
        // so /tasks doesn't accumulate stale "origin/feature-x" branches in
        // the branches listing.
        var fetch = GitDiscoverer.Discover(_repo).Single(c => c.Source == "git:demo:fetch");
        Assert.Equal(new[] { "-C", _repo, "fetch", "--all", "--prune" }, fetch.Args.ToArray());
        Assert.Equal("git_demo_fetch", fetch.SuggestedName);
    }

    [Fact]
    public void Discover_throws_when_directory_is_not_a_repo()
    {
        var notARepo = Path.Combine(_parent, "not-a-repo");
        Directory.CreateDirectory(notARepo);
        var ex = Assert.Throws<InvalidOperationException>(() => GitDiscoverer.Discover(notARepo));
        Assert.Contains("Not a git repository", ex.Message);
    }
}
