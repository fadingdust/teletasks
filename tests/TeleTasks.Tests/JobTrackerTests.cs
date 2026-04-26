using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TeleTasks.Configuration;
using TeleTasks.Models;
using TeleTasks.Services;
using TeleTasks.Services.Chat;
using Xunit;

namespace TeleTasks.Tests;

[Collection("EnvironmentMutating")]
public sealed class JobTrackerTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousEnv;
    private readonly List<int> _spawnedPids = new();

    public JobTrackerTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "teletasks-jobtracker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousEnv = Environment.GetEnvironmentVariable(UserConfigDirectory.EnvVar);
        Environment.SetEnvironmentVariable(UserConfigDirectory.EnvVar, _configDir);
    }

    public void Dispose()
    {
        // Best-effort cleanup of any sleep subprocesses that might still be alive
        // so we don't leak detached processes into the test host.
        foreach (var pid in _spawnedPids)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/kill",
                    ArgumentList = { "-KILL", $"-{pid}" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })?.WaitForExit(2000);
            }
            catch { }
        }

        Environment.SetEnvironmentVariable(UserConfigDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private JobTracker NewTracker() => new(NullLogger<JobTracker>.Instance);

    private static TaskDefinition Sleep(int seconds = 60, string name = "sleep_test")
    {
        return new TaskDefinition { Name = name };
    }

    private JobRecord StartSleep(JobTracker tracker, int seconds = 60, string name = "sleep_test")
    {
        var job = tracker.Start(
            Sleep(seconds, name),
            new Dictionary<string, object?>(),
            "/bin/sleep",
            new[] { seconds.ToString() });
        _spawnedPids.Add(job.Pid);
        return job;
    }

    private static bool ProcAlive(int pid) => File.Exists($"/proc/{pid}/stat");

    private static void WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            Thread.Sleep(50);
        }
    }

    // ─── Start ────────────────────────────────────────────────────────

    [Fact]
    public void Start_returns_a_job_record_with_a_real_pid()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        Assert.True(job.Id >= 1);
        Assert.True(job.Pid > 1);
        Assert.True(ProcAlive(job.Pid), "child process should still be alive immediately after Start");
        Assert.False(job.IsFinished);
    }

    [Fact]
    public void Start_creates_a_log_file_and_an_exit_code_path_under_LogDirectory()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        Assert.NotNull(job.LogPath);
        Assert.StartsWith(tracker.LogDirectory, job.LogPath);
        Assert.NotNull(job.ExitCodePath);
        Assert.StartsWith(tracker.LogDirectory, job.ExitCodePath);
    }

    [Fact]
    public void Start_persists_to_jobs_json_atomically()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        Assert.True(File.Exists(tracker.RegistryPath));
        var json = File.ReadAllText(tracker.RegistryPath);
        Assert.Contains($"\"id\": {job.Id}", json);
        Assert.Contains($"\"pid\": {job.Pid}", json);
        // No leftover .tmp file from the atomic rename.
        Assert.False(File.Exists(tracker.RegistryPath + ".tmp"));
    }

    [Fact]
    public void Start_assigns_increasing_unique_ids()
    {
        var tracker = NewTracker();
        var first  = StartSleep(tracker);
        var second = StartSleep(tracker);

        Assert.True(second.Id > first.Id);
    }

    // ─── Get / List ────────────────────────────────────────────────────

    [Fact]
    public void Get_returns_the_record_for_a_known_id()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);
        Assert.Same(job, tracker.Get(job.Id));
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_id()
    {
        Assert.Null(NewTracker().Get(99999));
    }

    [Fact]
    public void List_orders_active_jobs_before_finished_jobs()
    {
        var tracker = NewTracker();
        // Spawn a fast-finishing job, wait for it, then a still-running one.
        var quick = tracker.Start(Sleep(0, "fast"), new Dictionary<string, object?>(),
                                   "/bin/true", Array.Empty<string>());
        _spawnedPids.Add(quick.Pid);
        WaitFor(() => !ProcAlive(quick.Pid), TimeSpan.FromSeconds(3));
        tracker.Refresh();

        var alive = StartSleep(tracker);

        var ordered = tracker.List();
        Assert.Equal(alive.Id, ordered[0].Id);   // active first
        Assert.Equal(quick.Id, ordered[1].Id);   // finished second
    }

    // ─── Refresh ──────────────────────────────────────────────────────

    [Fact]
    public void Refresh_marks_a_completed_job_finished_and_recovers_its_exit_code()
    {
        var tracker = NewTracker();
        var job = tracker.Start(Sleep(0, "fast"), new Dictionary<string, object?>(),
                                 "/bin/sh", new[] { "-c", "exit 7" });
        _spawnedPids.Add(job.Pid);

        // Wait for the wrapper to finish writing the exit-code file.
        WaitFor(() =>
        {
            tracker.Refresh();
            var refreshed = tracker.Get(job.Id);
            return refreshed is not null && refreshed.IsFinished;
        }, TimeSpan.FromSeconds(5));

        var finished = tracker.Get(job.Id)!;
        Assert.True(finished.IsFinished);
        Assert.Equal(7, finished.ExitCode);
        Assert.False(finished.Killed);
    }

    [Fact]
    public void Refresh_does_not_fabricate_an_exit_code_for_a_killed_job()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        // External SIGKILL of the entire process tree — the wrapper bash never
        // gets to write the exitcode file. Mirrors the "system admin ran
        // kill -9 on it" path; distinct from /stop, which is the
        // tracker.Stop() pathway and sets Killed=true.
        try
        {
            using var p = Process.GetProcessById(job.Pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { /* already gone — fine */ }

        WaitFor(() =>
        {
            tracker.Refresh();
            var r = tracker.Get(job.Id);
            return r is not null && r.IsFinished;
        }, TimeSpan.FromSeconds(5));

        var finished = tracker.Get(job.Id)!;
        Assert.True(finished.IsFinished);
        Assert.Null(finished.ExitCode);   // wrapper never wrote one — exit unknown
        Assert.False(finished.Killed);     // we didn't /stop it ourselves
    }

    // ─── Stop ─────────────────────────────────────────────────────────

    [Fact]
    public void Stop_kills_a_running_job_and_marks_Killed_true()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        Assert.True(tracker.Stop(job.Id));

        WaitFor(() => !ProcAlive(job.Pid), TimeSpan.FromSeconds(5));
        Assert.False(ProcAlive(job.Pid));

        var stopped = tracker.Get(job.Id)!;
        Assert.True(stopped.IsFinished);
        Assert.True(stopped.Killed);
        Assert.Null(stopped.ExitCode);     // the wrapper never wrote one
    }

    [Fact]
    public void Stop_returns_false_for_an_already_finished_job()
    {
        var tracker = NewTracker();
        var job = tracker.Start(Sleep(0, "fast"), new Dictionary<string, object?>(),
                                 "/bin/true", Array.Empty<string>());
        _spawnedPids.Add(job.Pid);

        WaitFor(() =>
        {
            tracker.Refresh();
            return tracker.Get(job.Id)!.IsFinished;
        }, TimeSpan.FromSeconds(3));

        Assert.False(tracker.Stop(job.Id));
    }

    [Fact]
    public void Stop_returns_false_for_an_unknown_id()
    {
        Assert.False(NewTracker().Stop(99999));
    }

    // ─── TailLog ─────────────────────────────────────────────────────

    [Fact]
    public void TailLog_returns_the_last_N_lines_of_the_log_file()
    {
        var tracker = NewTracker();
        var job = tracker.Start(Sleep(0, "echo"), new Dictionary<string, object?>(),
                                 "/bin/sh", new[] { "-c", "for i in 1 2 3 4 5; do echo line $i; done" });
        _spawnedPids.Add(job.Pid);

        WaitFor(() => File.Exists(job.LogPath) && File.ReadAllText(job.LogPath).Contains("line 5"),
                TimeSpan.FromSeconds(5));

        var tail3 = tracker.TailLog(job.Id, 3);
        Assert.Contains("line 3", tail3);
        Assert.Contains("line 4", tail3);
        Assert.Contains("line 5", tail3);
        Assert.DoesNotContain("line 1", tail3);
    }

    [Fact]
    public void TailLog_returns_empty_for_an_unknown_id()
    {
        Assert.Equal(string.Empty, NewTracker().TailLog(99999, 30));
    }

    // ─── Mutators (chat / seen / completion) ──────────────────────────

    [Fact]
    public void AssignChat_records_chatId_and_persists()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        tracker.AssignChat(job.Id, chatId: ChatId.FromTelegram(42L));

        Assert.Equal(ChatId.FromTelegram(42L), tracker.Get(job.Id)!.ChatId);
        // ChatId now persists in canonical "provider:id" form, not as a bare long.
        Assert.Contains("\"chatId\": \"telegram:42\"", File.ReadAllText(tracker.RegistryPath));
    }

    [Fact]
    public void AssignChat_is_a_noop_for_unknown_id()
    {
        // Should not throw.
        NewTracker().AssignChat(99999, ChatId.FromTelegram(1L));
    }

    [Fact]
    public void RecordSeenArtifacts_unions_paths_skipping_duplicates()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        tracker.RecordSeenArtifacts(job.Id, new[] { "/a.png", "/b.png" });
        tracker.RecordSeenArtifacts(job.Id, new[] { "/b.png", "/c.png" });

        var seen = tracker.Get(job.Id)!.SeenArtifactPaths;
        Assert.Equal(new[] { "/a.png", "/b.png", "/c.png" }, seen.OrderBy(p => p).ToArray());
    }

    [Fact]
    public void RecordSeenArtifacts_ignores_null_or_empty_path_strings()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        tracker.RecordSeenArtifacts(job.Id, new[] { "", "/real.png", null! });

        Assert.Equal(new[] { "/real.png" }, tracker.Get(job.Id)!.SeenArtifactPaths.ToArray());
    }

    [Fact]
    public void MarkCompletionNotified_flips_flag_and_persists()
    {
        var tracker = NewTracker();
        var job = StartSleep(tracker);

        Assert.False(tracker.Get(job.Id)!.CompletionNotified);
        tracker.MarkCompletionNotified(job.Id);
        Assert.True(tracker.Get(job.Id)!.CompletionNotified);
        Assert.Contains("\"completionNotified\": true", File.ReadAllText(tracker.RegistryPath));
    }

    // ─── LoadAndReconcile (restart simulation) ─────────────────────────

    [Fact]
    public void LoadAndReconcile_picks_up_a_still_running_job_after_restart()
    {
        var tracker1 = NewTracker();
        var job = StartSleep(tracker1);

        // Construct a fresh tracker against the same config dir — simulates
        // bot restart with the child process still alive.
        var tracker2 = NewTracker();
        var rehydrated = tracker2.Get(job.Id);

        Assert.NotNull(rehydrated);
        Assert.Equal(job.Pid, rehydrated!.Pid);
        Assert.False(rehydrated.IsFinished);
    }

    [Fact]
    public void LoadAndReconcile_marks_dead_pids_finished()
    {
        var tracker1 = NewTracker();
        var job = tracker1.Start(Sleep(0, "fast"), new Dictionary<string, object?>(),
                                  "/bin/sh", new[] { "-c", "exit 0" });
        _spawnedPids.Add(job.Pid);

        // Wait for it to actually exit but DO NOT call tracker1.Refresh, so
        // the persisted record still says "running". The new tracker has to
        // figure that out from /proc on its own.
        WaitFor(() => !ProcAlive(job.Pid), TimeSpan.FromSeconds(3));

        var tracker2 = NewTracker();
        var rehydrated = tracker2.Get(job.Id);

        Assert.NotNull(rehydrated);
        Assert.True(rehydrated!.IsFinished);
        Assert.Equal(0, rehydrated.ExitCode);
    }

    [Fact]
    public void LoadAndReconcile_keeps_NextId_monotonic_across_restart()
    {
        var tracker1 = NewTracker();
        var first = StartSleep(tracker1);

        var tracker2 = NewTracker();
        var second = StartSleep(tracker2);

        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public void LoadAndReconcile_reads_legacy_long_chat_ids_as_telegram_provider()
    {
        // Pre-multi-provider, jobs.json stored chatId as a bare long. The
        // ChatIdJsonConverter accepts both forms; existing files survive
        // the upgrade without any user action.
        var registryPath = Path.Combine(_configDir, "jobs.json");
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(registryPath, """
            {
              "nextId": 2,
              "jobs": [
                {
                  "id": 1,
                  "taskName": "legacy_task",
                  "pid": 99999999,
                  "logPath": "/tmp/nonexistent.log",
                  "exitCodePath": "/tmp/nonexistent.exit",
                  "startedAt": "2025-01-01T12:00:00Z",
                  "finishedAt": "2025-01-01T12:00:01Z",
                  "exitCode": 0,
                  "chatId": 42,
                  "seenArtifacts": [],
                  "completionNotified": false
                }
              ]
            }
            """);

        var tracker = NewTracker();
        var job = tracker.Get(1);

        Assert.NotNull(job);
        Assert.Equal(ChatId.FromTelegram(42L), job!.ChatId);
    }
}
