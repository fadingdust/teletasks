using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class TaskMatcher
{
    public const string ShowTasksRoute = "_show_tasks";
    public const string ShowHelpRoute = "_show_help";
    public const string ShowResultsRoute = "_show_results";
    public const string ShowJobsRoute = "_show_jobs";
    public const string CheckLatestJobRoute = "_check_latest_job";

    private const string SystemPrompt = """
You are a strict request router for a personal Linux assistant bot.

For every message produce TWO fields:

  "intent" — what the user wants to DO (pick exactly one):
    Run      — execute a task
    Show     — view the latest output of a task without re-running it
    Status   — check what jobs / long-running tasks are active or how a
               specific job is going
    Stop     — kill a running job
    Restart  — re-run a previously finished job
    Cancel   — abort a pending parameter-collection prompt
    Help     — list tasks, show help, or explain what the bot can do

  "task" — which task the intent targets:
    • A REAL task name from the catalog for Run / Show / Restart.
    • For Stop: the task name whose active job to stop, or null for
      "stop whatever is running".
    • For Status: null (show all jobs) unless the user is clearly asking
      about one specific job — use "_check_latest_job" to indicate
      "how's the most recent one going?".
    • For Help: "_show_tasks" (list tasks) or "_show_help" (general help).
    • null for Cancel, or when no task can be identified.

Rules:
- Respond with a single JSON object matching the response schema. No prose.
- Only include parameter keys that the chosen task declares. Never invent
  parameters.
- NEVER invent string values. If a required parameter isn't in the message,
  OMIT it — the bot will ask. Empty string ("") is not a valid value.
- If most required parameters are missing, set "task" to null and explain
  in "reasoning".
- When in doubt between Run and another intent, prefer the other intent.
  It is much worse to run the wrong task than to ask the user to clarify.
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

    public static bool IsVirtualRoute(string? name) =>
        name == ShowTasksRoute || name == ShowHelpRoute || name == ShowResultsRoute ||
        name == ShowJobsRoute  || name == CheckLatestJobRoute;

    public async Task<TaskMatch?> MatchAsync(string userMessage, CancellationToken cancellationToken)
    {
        var prompt = BuildUserPrompt(userMessage);
        var schema = BuildResponseSchema();
        var raw = await _ollama.ChatStructuredAsync(SystemPrompt, prompt, schema, cancellationToken);

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
            var intent0 = ResolveIntent(payload?.Intent, null);
            return new TaskMatch(string.Empty, new Dictionary<string, object?>(), payload?.Reasoning, intent0);
        }

        var intent = ResolveIntent(payload.Intent, payload.Task);

        if (payload.Task == ShowTasksRoute || payload.Task == ShowHelpRoute ||
            payload.Task == ShowJobsRoute || payload.Task == CheckLatestJobRoute)
        {
            return new TaskMatch(payload.Task, new Dictionary<string, object?>(), payload.Reasoning, intent);
        }

        if (payload.Task == ShowResultsRoute)
        {
            // Carry through the task_name parameter (validated against the
            // catalog by the bot before evaluating the output spec, so a
            // hallucinated name can't slip through).
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (payload.Parameters is not null &&
                payload.Parameters.TryGetValue("task_name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                args["task_name"] = nameElement.GetString();
            }
            return new TaskMatch(payload.Task, args, payload.Reasoning, intent);
        }

        var task = _registry.Find(payload.Task);
        if (task is null)
        {
            _logger.LogWarning("Ollama selected unknown task '{Task}'", payload.Task);
            return new TaskMatch(string.Empty, new Dictionary<string, object?>(),
                $"Unknown task '{payload.Task}'.", TaskIntent.Run);
        }

        var coerced = CoerceParameters(task, payload.Parameters);
        return new TaskMatch(task.Name, coerced, payload.Reasoning, intent);
    }

    private string BuildUserPrompt(string userMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Intent guide:");
        sb.AppendLine("- Run: user wants to execute a task");
        sb.AppendLine("- Show: user wants to see latest output without re-running (e.g. \"show results\", \"what did X produce\")");
        sb.AppendLine("- Status: user asks what's running or how a job is doing");
        sb.AppendLine($"  • use task=\"{CheckLatestJobRoute}\" for \"how's the latest going?\" (no specific task named)");
        sb.AppendLine($"  • use task=null for \"list all jobs\"");
        sb.AppendLine("- Stop: user wants to kill a running job. task = name of the task whose job to stop, or null");
        sb.AppendLine("- Restart: user wants to re-run a finished job. task = the task name");
        sb.AppendLine("- Cancel: user wants to cancel a pending prompt");
        sb.AppendLine($"- Help: task=\"{ShowTasksRoute}\" for task list, task=\"{ShowHelpRoute}\" for instructions");
        sb.AppendLine();
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
        return sb.ToString();
    }

    private JsonNode BuildResponseSchema()
    {
        var taskNames = new JsonArray();
        foreach (var t in _registry.Tasks)
        {
            taskNames.Add(t.Name);
        }
        taskNames.Add(ShowTasksRoute);
        taskNames.Add(ShowHelpRoute);
        taskNames.Add(ShowResultsRoute);
        taskNames.Add(ShowJobsRoute);
        taskNames.Add(CheckLatestJobRoute);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["intent"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray
                    {
                        "Run", "Show", "Status", "Stop", "Restart", "Cancel", "Help"
                    }
                },
                ["task"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "string", ["enum"] = taskNames },
                        new JsonObject { ["type"] = "null" }
                    }
                },
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true
                },
                ["reasoning"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray { "intent", "task", "parameters", "reasoning" },
            ["additionalProperties"] = false
        };
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
        public string? Intent { get; set; }
        public string? Task { get; set; }
        public Dictionary<string, JsonElement>? Parameters { get; set; }
        public string? Reasoning { get; set; }
    }

    /// <summary>
    /// Resolve the intent from the payload. When the model emits an explicit
    /// intent string that parses to our enum, use it. Otherwise fall back to
    /// the legacy virtual-route alias so old model responses keep working.
    /// </summary>
    internal static TaskIntent ResolveIntent(string? intentStr, string? taskName) =>
        intentStr is not null &&
        Enum.TryParse<TaskIntent>(intentStr, ignoreCase: true, out var parsed)
            ? parsed
            : taskName switch
            {
                ShowResultsRoute               => TaskIntent.Show,
                ShowTasksRoute or ShowHelpRoute => TaskIntent.Help,
                ShowJobsRoute or CheckLatestJobRoute => TaskIntent.Status,
                _ => TaskIntent.Run
            };
}
