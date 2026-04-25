using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TeleTasks.Models;

namespace TeleTasks.Services;

public static class TaskCatalogWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    foreach (var prop in ti.Properties)
                    {
                        if (typeof(ICollection).IsAssignableFrom(prop.PropertyType))
                        {
                            prop.ShouldSerialize = (_, value) =>
                                value is ICollection c && c.Count > 0;
                        }
                    }
                }
            }
        }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static TaskCatalog Load(string path)
    {
        if (!File.Exists(path)) return new TaskCatalog();
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<TaskCatalog>(fs, ReadOptions) ?? new TaskCatalog();
    }

    public static int Merge(TaskCatalog catalog, IEnumerable<TaskDefinition> incoming)
    {
        var existing = new HashSet<string>(catalog.Tasks.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var task in incoming)
        {
            var name = task.Name;
            var i = 2;
            while (existing.Contains(name))
            {
                name = $"{task.Name}_{i++}";
            }
            task.Name = name;
            existing.Add(name);
            catalog.Tasks.Add(task);
            added++;
        }
        return added;
    }

    public static void Save(string path, TaskCatalog catalog)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(catalog, WriteOptions);
        File.WriteAllText(path, json);
    }

    public static string Render(TaskCatalog catalog) =>
        JsonSerializer.Serialize(catalog, WriteOptions);
}
