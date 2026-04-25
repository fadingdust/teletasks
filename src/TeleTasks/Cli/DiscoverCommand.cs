using System.Text.Json;
using System.Text.Json.Nodes;
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
You write short, friendly one-line documentation for personal Linux tasks.
Given a task and (optionally) its parameters, return:
- a single-sentence task description
- a single-sentence description for each parameter, keyed by name
Use plain English, no markdown, no command syntax. Reply with JSON only.
""";

        foreach (var c in candidates)
        {
            try
            {
                var prompt = BuildPolishPrompt(c);
                var schema = BuildPolishSchema(c);
                var raw = await ollama.ChatStructuredAsync(system, prompt, schema, cancellationToken);
                ApplyPolish(c, raw);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"# llm polish skipped for {c.Source}: {ex.Message}");
            }
        }
    }

    private static string BuildPolishPrompt(TaskCandidate c)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Source: ").AppendLine(c.Source);
        sb.Append("Name: ").AppendLine(c.SuggestedName);
        sb.Append("Current description: ").AppendLine(c.Description);
        sb.Append("Command: ").Append(c.Command).Append(' ').AppendLine(string.Join(' ', c.Args));
        if (c.Parameters.Count > 0)
        {
            sb.AppendLine("Parameters:");
            foreach (var p in c.Parameters)
            {
                sb.Append("  - ").Append(p.Name).Append(" (").Append(p.Type).Append(')');
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append(": ").Append(p.Description);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static JsonNode BuildPolishSchema(TaskCandidate c)
    {
        var paramProps = new JsonObject();
        foreach (var p in c.Parameters)
        {
            paramProps[p.Name] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["description"] = new JsonObject { ["type"] = "string" },
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = paramProps,
                    ["additionalProperties"] = false
                }
            },
            ["required"] = new JsonArray { "description" },
            ["additionalProperties"] = false
        };
        return schema;
    }

    private static void ApplyPolish(TaskCandidate c, string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return;

        try
        {
            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
            var root = doc.RootElement;
            if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var polished = d.GetString();
                if (!string.IsNullOrWhiteSpace(polished)) c.Description = polished.Trim();
            }
            if (root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in p.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var match = c.Parameters.FirstOrDefault(x =>
                        string.Equals(x.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    var text = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) match.Description = text.Trim();
                }
            }
        }
        catch (JsonException) { }
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
