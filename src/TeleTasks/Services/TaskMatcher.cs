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

Decide where to route the user's message:

  • A REAL task name from the catalog — only when the user is clearly asking
    to PERFORM an action that one task explicitly does. Extract parameters
    from the message into the "parameters" object.
  • "_show_results" — when the user is asking to SEE / SHOW / GET the most
    recent output of a specific task without running it again
    (e.g. "results from py_render", "show me my last screenshots from
    take_screenshot", "what did the build_logs task produce"). Put the
    target task's name in parameters as "task_name".
  • "_show_tasks" — when the user is asking what the bot can do, what
    tasks exist, what's available.
  • "_show_help" — when the user is asking for help or instructions.
  • "_show_jobs" — when the user is asking about running tasks / jobs /
    what's in progress (e.g. "what's running?", "list jobs", "anything
    still going?"). Only relevant for tasks marked longRunning.
  • "_check_latest_job" — when the user is asking how the most recent job
    is going (e.g. "how's the render?", "is it done yet?", "any progress?")
    without naming a task. Different from "_show_results" which targets
    a specific task by name.
  • null — for greetings, chit-chat, or anything the bot can't handle.

Rules:
- Respond with a single JSON object matching the response schema. No prose.
- Only include parameter keys that the chosen task declares. Never invent
  parameters.
- Use the parameter's declared type (string, integer, number, boolean).
- NEVER invent string values. A parameter's value MUST appear (paraphrased
  is OK) in the user's message. If you can't find a value for a required
  parameter in the message, OMIT that parameter entirely from "parameters"
  — the bot will ask the user. Empty string ("") is NOT a valid value;
  if the user didn't say it, leave the key out.
- If most required parameters are missing, set "task" to null and ask
  for them in "reasoning".
- "results from X", "what did X produce" with a SPECIFIC named task →
  "_show_results". "How's the latest run going" with no task named →
  "_check_latest_job".
- When in doubt between running a real task and one of the virtual routes,
  prefer the virtual route. It is much worse to run the wrong task than
  to ask the user to clarify.
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
            return new TaskMatch(string.Empty, new Dictionary<string, object?>(), payload?.Reasoning);
        }

        if (payload.Task == ShowTasksRoute || payload.Task == ShowHelpRoute ||
            payload.Task == ShowJobsRoute || payload.Task == CheckLatestJobRoute)
        {
            return new TaskMatch(payload.Task, new Dictionary<string, object?>(), payload.Reasoning);
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
            return new TaskMatch(payload.Task, args, payload.Reasoning);
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
        sb.AppendLine("Virtual routes (always available):");
        sb.AppendLine($"- {ShowTasksRoute}: route here when the user asks what tasks/commands exist");
        sb.AppendLine($"- {ShowHelpRoute}: route here when the user asks for help or instructions");
        sb.AppendLine($"- {ShowResultsRoute}: route here when the user wants to see the latest output of a specific task without running it. Set parameters.task_name to the task's name.");
        sb.AppendLine($"- {ShowJobsRoute}: route here when the user asks what jobs / long-running tasks are running");
        sb.AppendLine($"- {CheckLatestJobRoute}: route here when the user asks how a recent or current job is going");
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
            ["required"] = new JsonArray { "task", "parameters", "reasoning" },
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
        public string? Task { get; set; }
        public Dictionary<string, JsonElement>? Parameters { get; set; }
        public string? Reasoning { get; set; }
    }
}
