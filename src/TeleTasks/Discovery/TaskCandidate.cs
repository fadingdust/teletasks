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
}
