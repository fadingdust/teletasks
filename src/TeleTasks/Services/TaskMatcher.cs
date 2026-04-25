using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class TaskMatcher
{
    private const string SystemPrompt = """
You are a strict request router for a personal Linux assistant bot.

You will be given:
  1. A catalog of tasks. Each task has a name, a description, and (optionally) a list of parameters.
  2. A user message.

Your job: pick the single best-matching task and extract any parameter values.

Rules:
- Respond ONLY with a single JSON object. No prose, no markdown.
- Schema:
    {"task": "<task name or null>", "parameters": { "<name>": <value>, ... }, "reasoning": "<short>"}
- If no task fits, set "task" to null and explain in "reasoning".
- Only include parameter keys that the chosen task declares. Never invent parameters.
- Use the parameter's declared type (string, integer, number, boolean).
- If a parameter is required and missing from the message, set "task" to null and ask for it
  in "reasoning".
- Be conservative. If the user is just chatting, return null.
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly OllamaClient _ollama;
    private readonly TaskRegistry _registry;
    private readonly ILogger<TaskMatcher> _logger;

    public TaskMatcher(OllamaClient ollama, TaskRegistry registry, ILogger<TaskMatcher> logger)
    {
        _ollama = ollama;
        _registry = registry;
        _logger = logger;
    }

    public async Task<TaskMatch?> MatchAsync(string userMessage, CancellationToken cancellationToken)
    {
        var prompt = BuildUserPrompt(userMessage);
        var raw = await _ollama.ChatJsonAsync(SystemPrompt, prompt, cancellationToken);

        _logger.LogDebug("Ollama match raw response: {Raw}", raw);

        var json = ExtractJson(raw);
        if (json is null)
        {
            _logger.LogWarning("Could not extract JSON from Ollama response: {Raw}", raw);
            return null;
        }

        MatchPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MatchPayload>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ollama returned invalid JSON: {Raw}", raw);
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Task))
        {
            return new TaskMatch(string.Empty, new Dictionary<string, object?>(), payload?.Reasoning);
        }

        var task = _registry.Find(payload.Task);
        if (task is null)
        {
            _logger.LogWarning("Ollama selected unknown task '{Task}'", payload.Task);
            return new TaskMatch(string.Empty, new Dictionary<string, object?>(),
                $"Unknown task '{payload.Task}'.");
        }

        var coerced = CoerceParameters(task, payload.Parameters);
        return new TaskMatch(task.Name, coerced, payload.Reasoning);
    }

    private string BuildUserPrompt(string userMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Task catalog:");
        foreach (var task in _registry.Tasks)
        {
            sb.Append("- name: ").AppendLine(task.Name);
            sb.Append("  description: ").AppendLine(task.Description);
            if (task.Parameters.Count > 0)
            {
                sb.AppendLine("  parameters:");
                foreach (var p in task.Parameters)
                {
                    sb.Append("    - ").Append(p.Name)
                        .Append(" (").Append(p.Type).Append(p.Required ? ", required" : ", optional").Append(')');
                    if (!string.IsNullOrWhiteSpace(p.Description))
                    {
                        sb.Append(": ").Append(p.Description);
                    }
                    if (p.Enum is { Count: > 0 })
                    {
                        sb.Append(" [one of: ").Append(string.Join(", ", p.Enum)).Append(']');
                    }
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine();
        sb.Append("User message: ").AppendLine(userMessage);
        sb.AppendLine();
        sb.AppendLine("Respond with the JSON object now.");
        return sb.ToString();
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }

    private static Dictionary<string, object?> CoerceParameters(
        TaskDefinition task,
        Dictionary<string, JsonElement>? raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (raw is null) return result;

        foreach (var p in task.Parameters)
        {
            if (!raw.TryGetValue(p.Name, out var element))
            {
                if (p.Default is not null)
                {
                    result[p.Name] = p.Default;
                }
                continue;
            }

            result[p.Name] = Coerce(element, p.Type);
        }

        return result;
    }

    private static object? Coerce(JsonElement element, string type) =>
        (type.ToLowerInvariant(), element.ValueKind) switch
        {
            ("integer", JsonValueKind.Number) when element.TryGetInt64(out var l) => l,
            ("integer", JsonValueKind.String) when long.TryParse(element.GetString(), out var l) => l,
            ("number", JsonValueKind.Number) => element.GetDouble(),
            ("number", JsonValueKind.String) when double.TryParse(element.GetString(), out var d) => d,
            ("boolean", JsonValueKind.True) => true,
            ("boolean", JsonValueKind.False) => false,
            ("boolean", JsonValueKind.String) => element.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
            (_, JsonValueKind.String) => element.GetString(),
            (_, JsonValueKind.Number) when element.TryGetInt64(out var l) => l,
            (_, JsonValueKind.Number) => element.GetDouble(),
            (_, JsonValueKind.True) => true,
            (_, JsonValueKind.False) => false,
            (_, JsonValueKind.Null) => null,
            _ => element.GetRawText()
        };

    private sealed class MatchPayload
    {
        public string? Task { get; set; }
        public Dictionary<string, JsonElement>? Parameters { get; set; }
        public string? Reasoning { get; set; }
    }
}
