namespace TeleTasks.Models;

public sealed record OutputArtifact(
    string Kind,
    string? Path,
    string? Caption,
    string? Text);

public sealed class TaskExecutionResult
{
    public bool Success { get; set; }

    public int ExitCode { get; set; }

    public string? ErrorMessage { get; set; }

    public List<OutputArtifact> Artifacts { get; } = new();
}

public sealed record TaskMatch(
    string TaskName,
    Dictionary<string, object?> Parameters,
    string? Reasoning);
