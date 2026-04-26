using TeleTasks.Models;
using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public class TaskCatalogWriterTests
{
    private static TaskDefinition Task(string name, string? source = null, string? description = null, bool? enabled = null) => new()
    {
        Name = name,
        Source = source,
        Description = description ?? "",
        Enabled = enabled
    };

    [Fact]
    public void Merge_appends_a_new_task_when_no_existing_match()
    {
        var catalog = new TaskCatalog();
        var result = TaskCatalogWriter.Merge(catalog, new[] { Task("build", "Makefile:build") });

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Single(catalog.Tasks);
        Assert.Equal("build", catalog.Tasks[0].Name);
    }

    [Fact]
    public void Merge_updates_in_place_when_source_matches()
    {
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(new TaskDefinition
        {
            Name = "build",
            Source = "Makefile:build",
            Description = "old description"
        });

        var result = TaskCatalogWriter.Merge(catalog, new[] { Task("build", "Makefile:build", "new description") });

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Single(catalog.Tasks);
        Assert.Equal("new description", catalog.Tasks[0].Description);
    }

    [Fact]
    public void Merge_preserves_hand_edited_name_and_enabled_on_update()
    {
        // User renamed the discovered task locally and disabled it.
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(new TaskDefinition
        {
            Name = "my_special_build",        // hand-renamed
            Source = "Makefile:build",
            Description = "old",
            Enabled = false                   // hand-disabled
        });

        var incoming = new TaskDefinition
        {
            Name = "build",                   // discover would call it this
            Source = "Makefile:build",
            Description = "new"
        };
        TaskCatalogWriter.Merge(catalog, new[] { incoming });

        // Name and Enabled survive the merge.
        Assert.Equal("my_special_build", catalog.Tasks[0].Name);
        Assert.Equal(false, catalog.Tasks[0].Enabled);
        // Description is refreshed.
        Assert.Equal("new", catalog.Tasks[0].Description);
    }

    [Fact]
    public void Merge_renames_with_suffix_when_an_unsourced_task_owns_the_name()
    {
        // Hand-written task with no source occupies the name "build".
        // Discover would normally name its candidate "build" too — but the
        // hand-written one must NOT be touched. Incoming gets _2 instead.
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(new TaskDefinition
        {
            Name = "build",
            Source = null,                    // hand-written
            Description = "DO NOT TOUCH"
        });

        var result = TaskCatalogWriter.Merge(catalog, new[] { Task("build", "Makefile:build", "from discover") });

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Renamed);
        Assert.Equal(2, catalog.Tasks.Count);

        // Hand-written one is untouched.
        var handWritten = catalog.Tasks.Single(t => t.Source is null);
        Assert.Equal("build", handWritten.Name);
        Assert.Equal("DO NOT TOUCH", handWritten.Description);

        // Discovered one got the suffix.
        var discovered = catalog.Tasks.Single(t => t.Source == "Makefile:build");
        Assert.Equal("build_2", discovered.Name);
    }

    [Fact]
    public void Merge_is_idempotent_when_run_twice()
    {
        var catalog = new TaskCatalog();
        var first  = TaskCatalogWriter.Merge(catalog, new[] { Task("a", "Makefile:a"), Task("b", "Makefile:b") });
        var second = TaskCatalogWriter.Merge(catalog, new[] { Task("a", "Makefile:a"), Task("b", "Makefile:b") });

        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Updated);
        Assert.Equal(2, catalog.Tasks.Count);
    }

    [Fact]
    public void Merge_with_forceReplace_removes_stale_entries_in_the_same_category()
    {
        // An old "Makefile:test" target was renamed away and is no longer in the
        // Makefile. forceReplace nukes it before merge so the catalog stays clean.
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(Task("build", "Makefile:build"));
        catalog.Tasks.Add(Task("test",  "Makefile:test"));   // stale
        catalog.Tasks.Add(Task("logs",  "git:repo:logs"));    // different category, survives

        var result = TaskCatalogWriter.Merge(catalog, new[] { Task("build", "Makefile:build") }, forceReplace: true);

        Assert.Equal(2, result.Removed);   // both Makefile:* entries removed first
        Assert.Equal(1, result.Added);     // build re-added
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, catalog.Tasks.Count);   // build + logs
        Assert.Contains(catalog.Tasks, t => t.Source == "Makefile:build");
        Assert.Contains(catalog.Tasks, t => t.Source == "git:repo:logs");
    }

    [Fact]
    public void Merge_with_forceReplace_does_not_touch_hand_written_tasks()
    {
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(new TaskDefinition { Name = "scratch", Source = null });
        catalog.Tasks.Add(Task("test", "Makefile:test"));

        TaskCatalogWriter.Merge(catalog, new[] { Task("build", "Makefile:build") }, forceReplace: true);

        // Source-less task survives even with forceReplace (it has no category).
        Assert.Contains(catalog.Tasks, t => t.Name == "scratch" && t.Source is null);
    }

    [Fact]
    public void Merge_treats_nested_colons_in_source_as_part_of_the_category()
    {
        // git:repo-a:status and git:repo-a:log share category "git:repo-a";
        // git:repo-b:status is a different category and shouldn't be removed.
        var catalog = new TaskCatalog();
        catalog.Tasks.Add(Task("status_a", "git:repo-a:status"));
        catalog.Tasks.Add(Task("log_a",    "git:repo-a:log"));
        catalog.Tasks.Add(Task("status_b", "git:repo-b:status"));

        TaskCatalogWriter.Merge(catalog,
            new[] { Task("status_a", "git:repo-a:status") },
            forceReplace: true);

        Assert.Contains(catalog.Tasks, t => t.Source == "git:repo-b:status");
        // log_a was in the same category as the incoming, so it got pruned.
        Assert.DoesNotContain(catalog.Tasks, t => t.Source == "git:repo-a:log");
    }

    // ─── legacy-source migration ────────────────────────────────────────

    private static TaskDefinition TaskAt(string name, string source, string workingDirectory)
    {
        var t = Task(name, source);
        t.WorkingDirectory = workingDirectory;
        return t;
    }

    [Fact]
    public void Merge_upgrades_legacy_unscoped_source_to_the_new_project_scoped_form()
    {
        // Pre-scope shape: detectors used to emit "sh:run.sh". Post-scope:
        // "sh:proj:run.sh". The legacy entry must be updated in place,
        // not duplicated, so existing catalogs survive the upgrade.
        var legacy = TaskAt("sh_run", "sh:run.sh", "/home/me/proj");
        legacy.Description = "old description";
        legacy.Enabled = false;
        var catalog = new TaskCatalog { Tasks = { legacy } };

        var incoming = TaskAt("sh_proj_run", "sh:proj:run.sh", "/home/me/proj");
        incoming.Description = "fresh description";

        var result = TaskCatalogWriter.Merge(catalog, new[] { incoming });

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Single(catalog.Tasks);
        // Source rewritten to the new scoped form on the existing record.
        Assert.Equal("sh:proj:run.sh", catalog.Tasks[0].Source);
        // UpdateInPlace refreshes Description but keeps Name and Enabled.
        Assert.Equal("fresh description", catalog.Tasks[0].Description);
        Assert.Equal("sh_run", catalog.Tasks[0].Name);          // user's name preserved
        Assert.False(catalog.Tasks[0].Enabled);                  // user's enabled flag preserved
    }

    [Fact]
    public void Merge_does_not_upgrade_legacy_when_workingDirectory_disagrees()
    {
        // Same legacy source, different working directory — could be a stale
        // entry from an entirely different project. Don't claim it.
        var legacy = TaskAt("sh_run", "sh:run.sh", "/home/me/some-other-project");
        var catalog = new TaskCatalog { Tasks = { legacy } };

        var incoming = TaskAt("sh_proj_run", "sh:proj:run.sh", "/home/me/proj");

        var result = TaskCatalogWriter.Merge(catalog, new[] { incoming });

        Assert.Equal(1, result.Added);          // appended, not upgraded
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, catalog.Tasks.Count);
    }

    [Fact]
    public void Merge_handles_legacy_upgrade_for_multi_segment_source_prefixes()
    {
        // py:argparse:render.py → py:argparse:proj:render.py — the scope
        // is inserted before the filename, after the multi-segment prefix.
        var legacy = TaskAt("py_render", "py:argparse:render.py", "/home/me/proj");
        var catalog = new TaskCatalog { Tasks = { legacy } };

        var incoming = TaskAt("py_proj_render", "py:argparse:proj:render.py", "/home/me/proj");

        TaskCatalogWriter.Merge(catalog, new[] { incoming });

        Assert.Single(catalog.Tasks);
        Assert.Equal("py:argparse:proj:render.py", catalog.Tasks[0].Source);
    }

    [Fact]
    public void Merge_legacy_upgrade_only_runs_when_a_scoped_source_is_incoming()
    {
        // A second discover with the same un-scoped source (e.g. a hand-
        // written task or a downgraded build) should match its legacy peer
        // exactly without invoking the scope-strip fallback.
        var existing = TaskAt("sh_run", "sh:run.sh", "/home/me/proj");
        var catalog = new TaskCatalog { Tasks = { existing } };

        var incoming = TaskAt("sh_run", "sh:run.sh", "/home/me/proj");

        var result = TaskCatalogWriter.Merge(catalog, new[] { incoming });

        Assert.Equal(1, result.Updated);
        Assert.Single(catalog.Tasks);
        Assert.Equal("sh:run.sh", catalog.Tasks[0].Source);    // unchanged
    }
}
