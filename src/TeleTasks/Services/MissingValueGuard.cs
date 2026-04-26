using TeleTasks.Models;

namespace TeleTasks.Services;

/// <summary>
/// Decides whether a required parameter has a value that's safe to run
/// the task with. The bot's conversational prompt loop uses this to
/// figure out what to ask the user for.
///
/// "Missing" means any of:
///   * absent from the matcher's extracted values,
///   * present but null / empty whitespace (small LLMs sometimes emit ""
///     to satisfy a schema-required field),
///   * a string the model probably hallucinated — i.e. the value's tokens
///     don't appear in the user's original message after the task name is
///     stripped from the search space.
///
/// Numbers / booleans / enums skip the hallucination guard because their
/// schema-pinned valid space is small enough that hallucination is
/// structurally constrained, and word-form numbers ("five" → 5) wouldn't
/// pass a substring check anyway.
/// </summary>
public static class MissingValueGuard
{
    private static readonly char[] TokenSeparators =
        { ' ', '\t', '/', '\\', '.', '_', '-', ',' };

    public static bool HasUsableValue(
        TaskParameter parameter,
        IReadOnlyDictionary<string, object?> values,
        string userMessage,
        string? taskName = null)
    {
        if (!values.TryGetValue(parameter.Name, out var v)) return false;
        if (v is null) return false;
        if (v is not string s) return true;          // numbers / bools / enums
        if (string.IsNullOrWhiteSpace(s)) return false;

        // No user text → can't verify provenance, so fall back to the basic
        // null/whitespace check we already passed above.
        if (string.IsNullOrEmpty(userMessage)) return true;

        // Strip the task name from the search space. If the user typed only
        // the task name ("sh_run_local"), tokens of a hallucinated value
        // ("run.sh" → "run", "sh") that overlap with the task name itself
        // shouldn't be accepted as "the user said it". An empty residual
        // means every required string param is missing.
        var searchText = userMessage;
        if (!string.IsNullOrEmpty(taskName))
        {
            searchText = searchText.Replace(taskName, " ", StringComparison.OrdinalIgnoreCase);
        }
        if (string.IsNullOrWhiteSpace(searchText)) return false;

        // The matcher may legitimately paraphrase ("syslog" → "/var/log/syslog")
        // so we only require that any token of the value (>= 3 chars) appears
        // in the residual message.
        var tokens = s.Split(
                TokenSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .ToArray();
        if (tokens.Length == 0) return true;          // too short to verify, trust it
        foreach (var t in tokens)
        {
            if (searchText.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
