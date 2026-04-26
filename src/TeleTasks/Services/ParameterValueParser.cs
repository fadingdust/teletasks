using System.Globalization;
using TeleTasks.Models;

namespace TeleTasks.Services;

/// <summary>
/// Converts a raw chat message into the typed value a TaskParameter expects.
/// Returns either the coerced value or a friendly error explaining what was
/// wrong (so the bot can re-prompt with context).
/// </summary>
public static class ParameterValueParser
{
    public static bool TryParse(TaskParameter parameter, string raw, out object? value, out string? error)
    {
        var trimmed = raw.Trim();
        switch (parameter.Type.ToLowerInvariant())
        {
            case "integer":
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    value = l; error = null; return true;
                }
                value = null;
                error = $"'{Truncate(trimmed)}' isn't a valid integer.";
                return false;

            case "number":
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    value = d; error = null; return true;
                }
                value = null;
                error = $"'{Truncate(trimmed)}' isn't a valid number.";
                return false;

            case "boolean":
                var lower = trimmed.ToLowerInvariant();
                if (lower is "y" or "yes" or "true" or "1" or "on")
                {
                    value = true; error = null; return true;
                }
                if (lower is "n" or "no" or "false" or "0" or "off")
                {
                    value = false; error = null; return true;
                }
                value = null;
                error = $"'{Truncate(trimmed)}' isn't yes/no.";
                return false;

            default: // string and unknowns
                if (parameter.Enum is { Count: > 0 } choices)
                {
                    var match = choices.FirstOrDefault(c =>
                        string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        value = null;
                        error = $"'{Truncate(trimmed)}' isn't one of: {string.Join(", ", choices)}.";
                        return false;
                    }
                    value = match; error = null; return true;
                }
                // Empty / whitespace-only answer — the conversational loop is
                // collecting *required* parameters, so a blank value isn't
                // usable. Reprompt rather than running the task with "".
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    value = null;
                    error = "Please send a non-blank value.";
                    return false;
                }
                value = trimmed; error = null; return true;
        }
    }

    private static string Truncate(string s) =>
        s.Length <= 30 ? s : s[..30] + "…";
}
