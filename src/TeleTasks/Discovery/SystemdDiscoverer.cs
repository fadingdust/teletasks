using System.Diagnostics;
using TeleTasks.Models;

namespace TeleTasks.Discovery;

public static class SystemdDiscoverer
{
    public static async Task<IReadOnlyList<TaskCandidate>> DiscoverAsync(
        bool userScope,
        bool runningOnly,
        CancellationToken cancellationToken)
    {
        var units = await ListUnitsAsync(userScope, runningOnly, cancellationToken);
        var candidates = new List<TaskCandidate>
        {
            new()
            {
                Source = "systemd:journal",
                SuggestedName = userScope ? "journal_user" : "journal_system",
                Description = userScope
                    ? "Tail the user journal as a file."
                    : "Tail the system journal as a file.",
                Command = "/bin/bash",
                Args = new List<string>
                {
                    "-c",
                    userScope
                        ? "journalctl --user -n {lines} --no-pager > /tmp/teletasks-journal.txt"
                        : "journalctl -n {lines} --no-pager > /tmp/teletasks-journal.txt"
                },
                Parameters = new List<TaskParameter>
                {
                    new() { Name = "lines", Type = "integer", Default = 500L, Description = "How many journal lines" }
                },
                Output = new TaskOutputSpec
                {
                    Type = TaskOutputType.File,
                    Path = "/tmp/teletasks-journal.txt",
                    Caption = userScope ? "user journal" : "system journal"
                }
            }
        };

        foreach (var unit in units)
        {
            var safe = TaskCandidate.Sanitize(unit.Name.Replace(".service", ""));
            var args = userScope
                ? new List<string> { "-c", $"journalctl --user -u {Quote(unit.Name)} -n {{lines}} --no-pager" }
                : new List<string> { "-c", $"journalctl -u {Quote(unit.Name)} -n {{lines}} --no-pager" };

            candidates.Add(new TaskCandidate
            {
                Source = $"systemd:{unit.Name}",
                SuggestedName = TaskCandidate.Sanitize($"journal_{safe}"),
                Description = string.IsNullOrWhiteSpace(unit.Description)
                    ? $"Tail logs for `{unit.Name}`."
                    : $"Tail logs for `{unit.Name}` ({unit.Description}).",
                Command = "/bin/bash",
                Args = args,
                Parameters = new List<TaskParameter>
                {
                    new() { Name = "lines", Type = "integer", Default = 200L, Description = "How many lines" }
                },
                Output = new TaskOutputSpec
                {
                    Type = TaskOutputType.Text,
                    Caption = unit.Name
                }
            });
        }

        return candidates;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private sealed record SystemdUnit(string Name, string Description);

    private static async Task<List<SystemdUnit>> ListUnitsAsync(bool userScope, bool runningOnly, CancellationToken cancellationToken)
    {
        var args = new List<string> { "list-units", "--type=service", "--no-pager", "--no-legend", "--plain" };
        if (userScope) args.Add("--user");
        args.Add(runningOnly ? "--state=running" : "--all");

        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"# systemctl unavailable: {ex.Message}");
            return new List<SystemdUnit>();
        }
        if (proc is null) return new List<SystemdUnit>();

        using var _ = proc;
        var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        var units = new List<SystemdUnit>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var name = parts[0];
            if (!name.EndsWith(".service")) continue;

            var description = parts.Length >= 5 ? parts[4].Trim() : string.Empty;
            units.Add(new SystemdUnit(name, description));
        }
        return units;
    }
}
