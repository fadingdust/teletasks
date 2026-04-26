using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TeleTasks.Discovery;
using TeleTasks.Models;

namespace TeleTasks.Services;

public static class TaskCatalogWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    foreach (var prop in ti.Properties)
                    {
                        if (typeof(ICollection).IsAssignableFrom(prop.PropertyType))
                        {
                            prop.ShouldSerialize = (_, value) =>
                                value is ICollection c && c.Count > 0;
                        }
                    }
                }
            }
        }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static TaskCatalog Load(string path)
    {
        if (!File.Exists(path)) return new TaskCatalog();
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<TaskCatalog>(fs, ReadOptions) ?? new TaskCatalog();
    }

    public sealed record MergeResult(int Added, int Updated, int Renamed, int Removed);

    /// <summary>
    /// Re-run-safe merge for the discover commands.
    ///
    /// Each task carries a <see cref="TaskDefinition.Source"/> string that the detectors
    /// set (e.g. "Makefile:build", "git:teletasks:status"). On re-run we use that as the
    /// stable identity:
    /// 1. Existing task with the same source as an incoming candidate is updated in place
    ///    (description/command/args/parameters/output refreshed). Its <c>enabled</c> flag
    ///    and (potentially hand-edited) <c>name</c> are preserved.
    /// 2. Incoming candidate with no existing match is appended. Name conflicts get
    ///    suffixed with _2, _3, ... so a hand-written task with the same name isn't
    ///    overwritten.
    /// 3. With <paramref name="forceReplace"/>, existing tasks whose source belongs to
    ///    the same category as ANY incoming source are removed first. Category is the
    ///    string before the LAST colon (so "git:reponame" stays separate from
    ///    "git:other-repo"; "Makefile" matches all Makefile entries).
    /// </summary>
    public static MergeResult Merge(TaskCatalog catalog, IEnumerable<TaskDefinition> incoming, bool forceReplace = false)
    {
        var incomingList = incoming.ToList();
        var added = 0;
        var updated = 0;
        var renamed = 0;
        var removed = 0;

        if (forceReplace)
        {
            var categories = incomingList
                .Where(t => !string.IsNullOrEmpty(t.Source))
                .Select(t => SourceCategory(t.Source!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            removed = catalog.Tasks.RemoveAll(t =>
                !string.IsNullOrEmpty(t.Source) &&
                categories.Contains(SourceCategory(t.Source!)));
        }

        foreach (var task in incomingList)
        {
            TaskDefinition? bySource = null;
            if (!string.IsNullOrEmpty(task.Source))
            {
                bySource = catalog.Tasks.FirstOrDefault(t =>
                    string.Equals(t.Source, task.Source, StringComparison.OrdinalIgnoreCase));

                // Legacy-source migration: project-level detectors used to emit
                // un-scoped sources like "sh:run.sh" / "Makefile:build". After
                // adding project-scoped sources ("sh:<project>:run.sh"), an
                // existing legacy entry would otherwise be orphaned and a
                // duplicate created. If we find an entry whose source is the
                // un-scoped form AND whose workingDirectory matches the
                // incoming task's, treat it as a hit — UpdateInPlace will
                // rewrite the source to the new scoped form.
                if (bySource is null && !string.IsNullOrEmpty(task.WorkingDirectory))
                {
                    var scope = TaskCandidate.ProjectScope(task.WorkingDirectory!);
                    var legacy = LegacyUnscopedSource(task.Source!, scope);
                    if (legacy is not null)
                    {
                        bySource = catalog.Tasks.FirstOrDefault(t =>
                            string.Equals(t.Source, legacy, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(t.WorkingDirectory, task.WorkingDirectory, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            if (bySource is not null)
            {
                UpdateInPlace(bySource, task);
                updated++;
                continue;
            }

            var originalName = task.Name;
            var attempt = task.Name;
            var i = 2;
            while (catalog.Tasks.Any(t => string.Equals(t.Name, attempt, StringComparison.OrdinalIgnoreCase)))
            {
                attempt = $"{originalName}_{i++}";
            }
            if (!string.Equals(attempt, originalName, StringComparison.OrdinalIgnoreCase))
            {
                renamed++;
            }
            task.Name = attempt;
            catalog.Tasks.Add(task);
            added++;
        }

        return new MergeResult(added, updated, renamed, removed);
    }

    private static void UpdateInPlace(TaskDefinition existing, TaskDefinition incoming)
    {
        // Preserve user-set fields (Name and Enabled) — they may have hand-edits.
        existing.Description = incoming.Description;
        existing.Source = incoming.Source;
        existing.Command = incoming.Command;
        existing.WorkingDirectory = incoming.WorkingDirectory;
        existing.TimeoutSeconds = incoming.TimeoutSeconds;
        existing.Output = incoming.Output;

        existing.Args.Clear();
        existing.Args.AddRange(incoming.Args);

        existing.Parameters.Clear();
        existing.Parameters.AddRange(incoming.Parameters);

        existing.Env.Clear();
        foreach (var kv in incoming.Env) existing.Env[kv.Key] = kv.Value;
    }

    private static string SourceCategory(string source)
    {
        var idx = source.LastIndexOf(':');
        return idx > 0 ? source[..idx] : source;
    }

    /// <summary>
    /// Reverses the project-scope wrapping that detectors apply. Given a new-
    /// format source like <c>sh:projectA:run.sh</c> and the scope that produced
    /// it (<c>projectA</c>), returns the un-scoped equivalent <c>sh:run.sh</c>
    /// so the merge can find legacy entries written before scoping existed.
    /// Returns null when the scope segment isn't present in the source.
    /// </summary>
    private static string? LegacyUnscopedSource(string newSource, string scope)
    {
        if (string.IsNullOrEmpty(newSource) || string.IsNullOrEmpty(scope)) return null;
        var parts = newSource.Split(':');
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], scope, StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(":", parts.Take(i).Concat(parts.Skip(i + 1)));
            }
        }
        return null;
    }

    public static void Save(string path, TaskCatalog catalog)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(catalog, WriteOptions);
        File.WriteAllText(path, json);
    }

    public static string Render(TaskCatalog catalog) =>
        JsonSerializer.Serialize(catalog, WriteOptions);
}
