using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeleTasks.Configuration;
using TeleTasks.Discovery;
using TeleTasks.Models;
using TeleTasks.Services;

namespace TeleTasks.Cli;

public static class DiscoverCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var mode = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var opts = ParseOptions(rest);

        try
        {
            switch (mode)
            {
                case "project":
                    return await RunProjectAsync(opts, cancellationToken);
                case "systemd":
                    return await RunSystemdAsync(opts, cancellationToken);
                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown discover mode: {mode}");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"discover {mode} failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunProjectAsync(DiscoverOptions opts, CancellationToken cancellationToken)
    {
        var path = opts.Path ?? Directory.GetCurrentDirectory();
        Console.Error.WriteLine($"# scanning project: {Path.GetFullPath(path)}");
        var candidates = ProjectDiscoverer.Discover(path).ToList();
        return await EmitAsync(candidates, opts, cancellationToken);
    }

    private static async Task<int> RunSystemdAsync(DiscoverOptions opts, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"# scanning systemd ({(opts.UserScope ? "user" : "system")} scope, {(opts.RunningOnly ? "running" : "all")} units)");
        var candidates = await SystemdDiscoverer.DiscoverAsync(opts.UserScope, opts.RunningOnly, cancellationToken);
        return await EmitAsync(candidates, opts, cancellationToken);
    }

    private static async Task<int> EmitAsync(IReadOnlyList<TaskCandidate> candidates, DiscoverOptions opts, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("No tasks discovered.");
            return 0;
        }

        Console.Error.WriteLine($"# {candidates.Count} candidate(s):");
        foreach (var c in candidates)
        {
            Console.Error.WriteLine($"#   {c.Source} -> {c.SuggestedName}");
        }

        if (opts.UseLlm)
        {
            await PolishWithLlmAsync(candidates, cancellationToken);
        }

        var definitions = candidates.Select(c => c.ToDefinition()).ToList();

        if (opts.Write)
        {
            var path = opts.OutputPath ?? Path.Combine(Directory.GetCurrentDirectory(), "tasks.json");
            var catalog = TaskCatalogWriter.Load(path);
            var added = TaskCatalogWriter.Merge(catalog, definitions);
            TaskCatalogWriter.Save(path, catalog);
            Console.Error.WriteLine($"# wrote {added} task(s) to {path}");
        }
        else
        {
            var catalog = new TaskCatalog();
            TaskCatalogWriter.Merge(catalog, definitions);
            Console.WriteLine(TaskCatalogWriter.Render(catalog));
        }
        return 0;
    }

    private static async Task PolishWithLlmAsync(IReadOnlyList<TaskCandidate> candidates, CancellationToken cancellationToken)
    {
        var sp = BuildLlmServices();
        var ollama = sp.GetRequiredService<OllamaClient>();
        const string system = """
You write short, friendly one-line descriptions of personal Linux tasks.
Reply with strict JSON: {"description": "<one short sentence>"}. No prose.
""";

        foreach (var c in candidates)
        {
            try
            {
                var prompt = $"Source: {c.Source}\nName: {c.SuggestedName}\nCurrent description: {c.Description}\nCommand: {c.Command} {string.Join(' ', c.Args)}";
                var raw = await ollama.ChatJsonAsync(system, prompt, cancellationToken);
                if (TryExtractField(raw, "description", out var polished) && !string.IsNullOrWhiteSpace(polished))
                {
                    c.Description = polished.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"# llm polish skipped for {c.Source}: {ex.Message}");
            }
        }
    }

    private static bool TryExtractField(string raw, string field, out string value)
    {
        value = string.Empty;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        try
        {
            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
            if (doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            {
                value = v.GetString() ?? string.Empty;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static IServiceProvider BuildLlmServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables(prefix: "TELETASKS_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true));
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
        services.AddHttpClient(OllamaClient.HttpClientName);
        services.AddSingleton<OllamaClient>();
        return services.BuildServiceProvider();
    }

    private static DiscoverOptions ParseOptions(string[] args)
    {
        var opts = new DiscoverOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                case "-p":
                    opts.Path = args[++i];
                    break;
                case "--write":
                case "-w":
                    opts.Write = true;
                    break;
                case "--output":
                case "-o":
                    opts.OutputPath = args[++i];
                    opts.Write = true;
                    break;
                case "--llm":
                    opts.UseLlm = true;
                    break;
                case "--no-llm":
                    opts.UseLlm = false;
                    break;
                case "--user":
                    opts.UserScope = true;
                    break;
                case "--running":
                    opts.RunningOnly = true;
                    break;
                case "--all":
                    opts.RunningOnly = false;
                    break;
                default:
                    if (opts.Path is null && !args[i].StartsWith('-'))
                    {
                        opts.Path = args[i];
                    }
                    break;
            }
        }
        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
Usage: teletasks discover <mode> [options]

Modes:
  project [--path DIR]    scan a directory for Makefile/justfile/package.json/
                          pyproject.toml scripts, *.sh, and .vscode/tasks.json
  systemd [--user]        emit journalctl tail tasks per systemd unit
                          [--running|--all]   only running units (default: all)

Common options:
  --write, -w             append discovered tasks to tasks.json (instead of stdout)
  --output, -o PATH       write to a specific tasks.json path (implies --write)
  --llm                   ask Ollama to refine descriptions (off by default)
  --no-llm                disable LLM (default)

Examples:
  teletasks discover project --path ~/projects/scripts
  teletasks discover systemd --user --running
  teletasks discover project -w           # append to ./tasks.json
""");
    }

    private sealed class DiscoverOptions
    {
        public string? Path { get; set; }
        public bool Write { get; set; }
        public string? OutputPath { get; set; }
        public bool UseLlm { get; set; }
        public bool UserScope { get; set; }
        public bool RunningOnly { get; set; }
    }
}
