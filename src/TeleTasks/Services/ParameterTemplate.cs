using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeleTasks.Services;

public static class ParameterTemplate
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static string Apply(string template, IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v) && v is not null)
            {
                return Format(v);
            }
            return m.Value;
        });
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
