using System.Text.Json;

namespace TeleTasks.Discovery.Detectors;

public static class VsCodeTasksDetector
{
    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        var path = Path.Combine(projectPath, ".vscode", "tasks.json");
        if (!File.Exists(path)) yield break;

        JsonDocument doc;
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            doc = JsonDocument.Parse(File.ReadAllText(path), options);
        }
        catch (JsonException) { yield break; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array) yield break;

            foreach (var entry in tasks.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var label = entry.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? "task"
                    : "task";

                var command = entry.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(command)) continue;

                var args = new List<string>();
                if (entry.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in argsEl.EnumerateArray())
                    {
                        switch (a.ValueKind)
                        {
                            case JsonValueKind.String:
                                args.Add(a.GetString() ?? "");
                                break;
                            case JsonValueKind.Object:
                                if (a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                                    args.Add(v.GetString() ?? "");
                                break;
                        }
                    }
                }

                var detail = entry.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                yield return new TaskCandidate
                {
                    Source = $".vscode/tasks.json:{label}",
                    SuggestedName = TaskCandidate.Sanitize($"vsc_{label}"),
                    Description = detail ?? $"VS Code task: {label}",
                    Command = command,
                    Args = args,
                    WorkingDirectory = projectPath
                };
            }
        }
    }
}
