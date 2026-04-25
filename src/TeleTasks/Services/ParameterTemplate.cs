using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeleTasks.Services;

public static class ParameterTemplate
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Substitute <c>{name}</c> placeholders, iterating until stable so a value
    /// like <c>"./results/{lora}/output"</c> stored as a parameter default for
    /// <c>output_dir</c> still gets <c>{lora}</c> resolved when something else
    /// references <c>{output_dir}</c>. Caps at 5 passes so a cycle
    /// (e.g. a → {b}, b → {a}) can't spin forever.
    /// </summary>
    public static string Apply(string template, IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var current = template;
        for (var pass = 0; pass < 5; pass++)
        {
            var next = Placeholder.Replace(current, m =>
            {
                var key = m.Groups[1].Value;
                if (values.TryGetValue(key, out var v) && v is not null)
                {
                    return Format(v);
                }
                return m.Value;
            });
            if (string.Equals(next, current, StringComparison.Ordinal)) return current;
            current = next;
        }
        return current;
    }

    public static IReadOnlyList<string> ApplyAll(IEnumerable<string> templates, IReadOnlyDictionary<string, object?> values) =>
        templates.Select(t => Apply(t, values)).ToList();

    private static string Format(object value) =>
        value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
}
