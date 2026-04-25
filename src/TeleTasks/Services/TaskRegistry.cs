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

    public string ResolvedPath => ResolvePath();

    public void Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Task catalog not found at '{path}'. Set TaskCatalog:Path or place a tasks.json next to the binary.",
                path);
        }

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

    public TaskDefinition? Find(string name) =>
        _tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private string ResolvePath()
    {
        var configured = _options.Path;
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        // Prefer the user config directory (where `discover -w` writes by default,
        // and where users edit by hand), falling back to ContentRoot then bin.
        var userConfigPath = Path.Combine(UserConfigDirectory.Resolve(), configured);
        if (File.Exists(userConfigPath))
        {
            return userConfigPath;
        }

        var contentRootPath = Path.Combine(_env.ContentRootPath, configured);
        if (File.Exists(contentRootPath))
        {
            return contentRootPath;
        }

        var binPath = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(binPath))
        {
            return binPath;
        }

        // Nothing exists yet — return the user-config path so a future write goes there.
        return userConfigPath;
    }

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
