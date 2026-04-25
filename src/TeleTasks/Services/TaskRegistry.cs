using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;

namespace TeleTasks.Services;

public sealed class TaskRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly TaskCatalogOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<TaskRegistry> _logger;

    private IReadOnlyList<TaskDefinition> _tasks = Array.Empty<TaskDefinition>();
    private IReadOnlyList<TaskDefinition> _disabledTasks = Array.Empty<TaskDefinition>();
    private DateTime _loadedAtUtc;

    public TaskRegistry(
        IOptions<TaskCatalogOptions> options,
        IHostEnvironment env,
        ILogger<TaskRegistry> logger)
    {
        _options = options.Value;
        _env = env;
        _logger = logger;
    }

    public IReadOnlyList<TaskDefinition> Tasks => _tasks;

    public IReadOnlyList<TaskDefinition> DisabledTasks => _disabledTasks;

    public DateTime LoadedAtUtc => _loadedAtUtc;

    public void Load()
    {
        var path = EnsureCatalog();

        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<TaskCatalog>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Empty task catalog at '{path}'.");

        Validate(catalog);

        _tasks = catalog.Tasks.Where(t => t.IsEnabled).ToList();
        _disabledTasks = catalog.Tasks.Where(t => !t.IsEnabled).ToList();
        _loadedAtUtc = DateTime.UtcNow;
        _logger.LogInformation(
            "Loaded {Count} task(s) from {Path} ({Disabled} disabled).",
            _tasks.Count, path, _disabledTasks.Count);
    }

    /// <summary>
    /// Always resolve the catalog to the user-config-dir copy. If it's missing,
    /// seed it from the bundled bin default (so first-run users see the example
    /// task), otherwise create an empty catalog. An absolute TaskCatalog:Path is
    /// treated as user-pinned and not auto-created.
    /// </summary>
    private string EnsureCatalog()
    {
        var configured = _options.Path;

        if (Path.IsPathRooted(configured))
        {
            if (!File.Exists(configured))
            {
                throw new FileNotFoundException(
                    $"Task catalog not found at '{configured}' (TaskCatalog:Path is absolute; " +
                    "TeleTasks won't bootstrap a default at an absolute path you set explicitly).",
                    configured);
            }
            return configured;
        }

        var target = Path.Combine(UserConfigDirectory.Resolve(), configured);
        if (File.Exists(target)) return target;

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var binPath = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(binPath) && !string.Equals(binPath, target, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(binPath, target);
                _logger.LogInformation(
                    "Seeded task catalog at {Target} from bundled default {Source}.",
                    target, binPath);
                return target;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to seed {Target} from {Source}; loading directly from bin.",
                    target, binPath);
                return binPath;
            }
        }

        File.WriteAllText(target, "{\n  \"tasks\": []\n}\n");
        _logger.LogInformation("Created empty task catalog at {Path}.", target);
        return target;
    }

    public TaskDefinition? Find(string name) =>
        _tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Validate(TaskCatalog catalog)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in catalog.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Name))
            {
                throw new InvalidOperationException("Every task must have a non-empty 'name'.");
            }

            if (task.Name.StartsWith('_'))
            {
                throw new InvalidOperationException(
                    $"Task name '{task.Name}' starts with '_' which is reserved for built-in matcher routes.");
            }

            if (!seen.Add(task.Name))
            {
                throw new InvalidOperationException($"Duplicate task name '{task.Name}'.");
            }

            var paramSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in task.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    throw new InvalidOperationException($"Task '{task.Name}' has a parameter with no name.");
                }

                if (!paramSeen.Add(p.Name))
                {
                    throw new InvalidOperationException(
                        $"Task '{task.Name}' has duplicate parameter '{p.Name}'.");
                }
            }

            switch (task.Output.Type)
            {
                case TaskOutputType.File:
                case TaskOutputType.Image:
                case TaskOutputType.LogTail:
                    if (string.IsNullOrWhiteSpace(task.Output.Path))
                    {
                        throw new InvalidOperationException(
                            $"Task '{task.Name}' output type '{task.Output.Type}' requires 'path'.");
                    }
                    break;
                case TaskOutputType.Images:
                    if (string.IsNullOrWhiteSpace(task.Output.Directory))
                    {
                        throw new InvalidOperationException(
                            $"Task '{task.Name}' output type 'Images' requires 'directory'.");
                    }
                    break;
            }
        }
    }
}
