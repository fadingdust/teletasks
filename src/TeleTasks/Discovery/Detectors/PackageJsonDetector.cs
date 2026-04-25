using System.Text.Json;

namespace TeleTasks.Discovery.Detectors;

public static class PackageJsonDetector
{
    public static IEnumerable<TaskCandidate> Detect(string projectPath)
    {
        var path = Path.Combine(projectPath, "package.json");
        if (!File.Exists(path)) yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object) yield break;

            foreach (var script in scripts.EnumerateObject())
            {
                var name = script.Name;
                var body = script.Value.ValueKind == JsonValueKind.String
                    ? script.Value.GetString() ?? string.Empty
                    : string.Empty;

                yield return new TaskCandidate
                {
                    Source = $"package.json:{name}",
                    SuggestedName = TaskCandidate.Sanitize($"npm_{name}"),
                    Description = string.IsNullOrEmpty(body)
                        ? $"Run `npm run {name}`."
                        : $"Run `npm run {name}` ({body}).",
                    Command = "/usr/bin/env",
                    Args = new List<string> { "npm", "run", name },
                    WorkingDirectory = projectPath
                };
            }
        }
    }
}
