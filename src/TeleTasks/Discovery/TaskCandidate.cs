using TeleTasks.Models;

namespace TeleTasks.Discovery;

public sealed class TaskCandidate
{
    public required string Source { get; init; }

    public required string SuggestedName { get; init; }

    public string Description { get; set; } = string.Empty;

    public string? Command { get; init; }

    public List<string> Args { get; init; } = new();

    public string? WorkingDirectory { get; init; }

    public List<TaskParameter> Parameters { get; init; } = new();

    public TaskOutputSpec Output { get; set; } = new() { Type = TaskOutputType.Text };

    /// <summary>
    /// Set by the interactive review prompt. Null means "use the
    /// TaskDefinition default" (enabled). Surfaced to tasks.json as
    /// <c>enabled: false</c> when explicitly disabled by the user.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Set by the interactive review prompt or future heuristics. Null
    /// means "use the TaskDefinition default" (not long-running). Surfaced
    /// to tasks.json as <c>longRunning: true</c> when set.
    /// </summary>
    public bool? LongRunning { get; set; }

    /// <summary>
    /// Discovery-only context: the script/recipe/file body that this candidate
    /// was extracted from. Not serialized into tasks.json, but fed to the LLM
    /// during --llm polish so it can write concrete parameter descriptions
    /// based on how $1, $2, --flag, etc. are actually used.
    /// </summary>
    public string? SourceText { get; set; }

    public TaskDefinition ToDefinition()
    {
        var task = new TaskDefinition
        {
            Name = SuggestedName,
            Description = Description,
            Source = Source,
            Command = Command,
            WorkingDirectory = WorkingDirectory,
            Output = Output,
            Enabled = Enabled,
            LongRunning = LongRunning
        };
        task.Args.AddRange(Args);
        task.Parameters.AddRange(Parameters);
        return task;
    }

    public static string Sanitize(string raw)
    {
        var chars = raw
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var name = new string(chars).Trim('_');
        while (name.Contains("__"))
        {
            name = name.Replace("__", "_");
        }
        if (name.Length == 0) name = "task";
        if (char.IsDigit(name[0])) name = "_" + name;
        return name;
    }

    /// <summary>
    /// Sanitised basename of a project directory, used by project-level
    /// detectors (Makefile / sh / argparse Python / package.json /
    /// pyproject.toml / justfile / .vscode/tasks.json) to scope their
    /// <c>Source</c> and <c>SuggestedName</c> so two projects that each
    /// have a <c>run.sh</c> or a <c>build</c> Make target don't collide
    /// in the catalogue. Same pattern Git discovery has always used
    /// (<c>git:&lt;reponame&gt;:status</c>).
    ///
    /// Returns "project" as a safe fallback when the path can't be
    /// resolved to a meaningful basename (e.g. discover invoked at "/").
    /// </summary>
    public static string ProjectScope(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return "project";
        string absolute;
        try { absolute = Path.GetFullPath(projectPath); }
        catch { return "project"; }
        var trimmed = absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return "project";
        var sanitised = Sanitize(name);
        return string.IsNullOrEmpty(sanitised) ? "project" : sanitised;
    }
}
