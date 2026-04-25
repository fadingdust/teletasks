using System.Diagnostics;
using System.Text.Json;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

public static class GitDiscoverer
{
    public static IReadOnlyList<TaskCandidate> Discover(string repoPath)
    {
        var absolute = Path.GetFullPath(repoPath);
        if (!Directory.Exists(Path.Combine(absolute, ".git")) &&
            !File.Exists(Path.Combine(absolute, ".git")))
        {
            throw new InvalidOperationException(
                $"Not a git repository: {absolute}. Pass --path to a repo root.");
        }

        var name = TaskCandidate.Sanitize(
            Path.GetFileName(absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (string.IsNullOrEmpty(name)) name = "repo";

        var ghAvailable = ResolveOnPath("gh") is not null;
        var diffPath = $"/tmp/teletasks-{name}-diff.patch";
        var list = new List<TaskCandidate>
        {
            new()
            {
                Source = $"git:{name}:status",
                SuggestedName = $"git_{name}_status",
                Description = $"Show git status (branch + modified files) for {name}.",
                Command = "/usr/bin/git",
                Args = new List<string> { "-C", absolute, "status", "--short", "--branch" },
                WorkingDirectory = absolute,
                Output = new TaskOutputSpec { Type = TaskOutputType.Text }
            },
            new()
            {
                Source = $"git:{name}:log",
                SuggestedName = $"git_{name}_log",
                Description = $"Show recent commits in {name}.",
                Command = "/usr/bin/git",
                Args = new List<string> { "-C", absolute, "log", "--oneline", "--decorate", "-n", "{count}" },
                WorkingDirectory = absolute,
                Parameters = new List<TaskParameter>
                {
                    new() { Name = "count", Type = "integer", Default = 10L, Description = "Number of commits" }
                },
                Output = new TaskOutputSpec { Type = TaskOutputType.Text }
            },
            new()
            {
                Source = $"git:{name}:diff",
                SuggestedName = $"git_{name}_diff",
                Description = $"Send the uncommitted diff in {name} as a file.",
                Command = "/bin/bash",
                Args = new List<string>
                {
                    "-c",
                    $"git -C {ShellQuote(absolute)} diff --no-color > {ShellQuote(diffPath)}"
                },
                WorkingDirectory = absolute,
                Output = new TaskOutputSpec
                {
                    Type = TaskOutputType.File,
                    Path = diffPath,
                    Caption = $"{name} diff"
                }
            },
            new()
            {
                Source = $"git:{name}:branches",
                SuggestedName = $"git_{name}_branches",
                Description = $"List branches with their last commit in {name}.",
                Command = "/usr/bin/git",
                Args = new List<string> { "-C", absolute, "branch", "-vv", "--sort=-committerdate" },
                WorkingDirectory = absolute,
                Output = new TaskOutputSpec { Type = TaskOutputType.Text }
            }
        };

        if (ghAvailable)
        {
            list.Add(new TaskCandidate
            {
                Source = $"git:{name}:gh-runs",
                SuggestedName = $"gh_{name}_runs",
                Description = $"Show recent GitHub Actions runs for {name}.",
                Command = "/usr/bin/env",
                Args = new List<string> { "gh", "run", "list", "-R", absolute, "-L", "{count}" },
                WorkingDirectory = absolute,
                Parameters = new List<TaskParameter>
                {
                    new() { Name = "count", Type = "integer", Default = 10L, Description = "Number of runs" }
                },
                Output = new TaskOutputSpec { Type = TaskOutputType.Text }
            });

            list.Add(new TaskCandidate
            {
                Source = $"git:{name}:gh-prs",
                SuggestedName = $"gh_{name}_prs",
                Description = $"List open pull requests for {name}.",
                Command = "/usr/bin/env",
                Args = new List<string> { "gh", "pr", "list", "-R", absolute },
                WorkingDirectory = absolute,
                Output = new TaskOutputSpec { Type = TaskOutputType.Text }
            });
        }

        return list;
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string? ResolveOnPath(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/env",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("which");
        psi.ArgumentList.Add(command);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return p.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch { return null; }
    }
}
