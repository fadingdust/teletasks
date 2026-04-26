using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeleTasks.Models;

namespace TeleTasks.Services;

/// <summary>
/// Reads sidecar JSON files for a batch of images and (optionally) computes
/// the constant-vs-variable split across them.
///
/// Constant fields = the same scalar value in every sidecar.
/// Variable fields = at least one differing value (or missing in some).
///
/// Designed for ML/render outputs where every image has a paired
/// <c>NNNN.json</c> with prompt / seed / model / dimensions / etc.
/// </summary>
public static class SidecarMetadata
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_\.\-]*)\}", RegexOptions.Compiled);

    public sealed record SidecarBatch(
        IReadOnlyDictionary<string, string> Constant,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Variable,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Full);

    public static SidecarBatch Read(IReadOnlyList<string> imagePaths, string sidecarExtension)
    {
        var ext = NormalizeExtension(sidecarExtension);
        var fulls = new List<IReadOnlyDictionary<string, string>>(imagePaths.Count);
        foreach (var image in imagePaths)
        {
            var sidecarPath = SiblingPath(image, ext);
            fulls.Add(ReadFlatScalars(sidecarPath));
        }
        var (constant, variable) = ComputeDiff(fulls);
        return new SidecarBatch(constant, variable, fulls);
    }

    /// <summary>
    /// Build a caption from the chosen field map. If a template is present,
    /// substitute <c>{key}</c> placeholders; else format as
    /// <c>key1: value1, key2: value2</c>. Truncates to <paramref name="maxLength"/>.
    /// </summary>
    public static string BuildCaption(IReadOnlyDictionary<string, string> fields, string? template, int maxLength)
    {
        if (fields.Count == 0 && string.IsNullOrEmpty(template)) return string.Empty;

        string raw;
        if (!string.IsNullOrWhiteSpace(template))
        {
            raw = Placeholder.Replace(template, m =>
            {
                var key = m.Groups[1].Value;
                return fields.TryGetValue(key, out var v) ? v : m.Value;
            });
        }
        else
        {
            raw = string.Join(", ", fields
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}: {Truncate(kv.Value, 80)}"));
        }

        if (maxLength > 0 && raw.Length > maxLength) raw = raw[..(maxLength - 3)] + "...";
        return raw;
    }

    public static string SiblingPath(string imagePath, string extension)
    {
        // Returns the path of the best-matching sibling file for an image.
        // Tries the direct change-extension form first, then progressively
        // strips trailing index suffixes from the image stem so a render
        // batch like:
        //   render-20250101_1200_00.png  →  render-20250101_1200.json
        // pairs correctly. If nothing matches, returns the direct form
        // (callers like ReadFlatScalars will see it as missing → empty).
        var ext = NormalizeExtension(extension);
        var dir = Path.GetDirectoryName(imagePath) ?? string.Empty;
        var bare = Path.GetFileNameWithoutExtension(imagePath);

        var direct = Path.Combine(dir, bare + ext);
        if (File.Exists(direct)) return direct;

        // Walk up: strip _NN, .NN, -NN from the tail repeatedly
        var stem = bare;
        for (var i = 0; i < 3; i++)
        {
            var stripped = StripTrailingIndex(stem);
            if (stripped == stem) break;
            stem = stripped;
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }

        return direct;
    }

    private static readonly Regex TrailingIndex = new(@"^(.+?)[_\-.]\d+$", RegexOptions.Compiled);

    private static string StripTrailingIndex(string name)
    {
        var m = TrailingIndex.Match(name);
        return m.Success ? m.Groups[1].Value : name;
    }

    private static string NormalizeExtension(string ext) =>
        ext.StartsWith('.') ? ext : "." + ext;

    private static IReadOnlyDictionary<string, string> ReadFlatScalars(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return new Dictionary<string, string>(0);

        try
        {
            using var fs = File.OpenRead(sidecarPath);
            using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>(0);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (TryFormatScalar(prop.Value, out var formatted))
                {
                    map[prop.Name] = formatted!;
                }
            }
            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(0);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(0);
        }
    }

    private static bool TryFormatScalar(JsonElement element, out string? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                value = element.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : element.GetDouble().ToString(CultureInfo.InvariantCulture);
                return true;
            case JsonValueKind.True:
                value = "true";
                return true;
            case JsonValueKind.False:
                value = "false";
                return true;
            case JsonValueKind.Null:
                value = null;
                return false;
            default:
                value = null;
                return false;
        }
    }

    private static (IReadOnlyDictionary<string, string> Constant, IReadOnlyList<IReadOnlyDictionary<string, string>> Variable)
        ComputeDiff(IReadOnlyList<IReadOnlyDictionary<string, string>> sidecars)
    {
        if (sidecars.Count == 0)
            return (new Dictionary<string, string>(0), Array.Empty<IReadOnlyDictionary<string, string>>());

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sidecars)
            foreach (var k in s.Keys)
                allKeys.Add(k);

        var constant = new Dictionary<string, string>(StringComparer.Ordinal);
        var variableKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in allKeys)
        {
            string? first = null; bool firstSet = false; bool allSame = true;
            bool everyoneHas = true;
            foreach (var s in sidecars)
            {
                if (!s.TryGetValue(key, out var v))
                {
                    everyoneHas = false; allSame = false; break;
                }
                if (!firstSet) { first = v; firstSet = true; }
                else if (!string.Equals(first, v, StringComparison.Ordinal)) { allSame = false; break; }
            }

            if (allSame && everyoneHas && first is not null)
            {
                constant[key] = first;
            }
            else
            {
                variableKeys.Add(key);
            }
        }

        var variable = sidecars.Select<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>(s =>
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in variableKeys)
            {
                if (s.TryGetValue(key, out var v)) d[key] = v;
            }
            return d;
        }).ToList();

        return (constant, variable);
    }

    private static string Truncate(string value, int max)
    {
        if (max <= 0 || value.Length <= max) return value;
        return value[..max] + "...";
    }
}
