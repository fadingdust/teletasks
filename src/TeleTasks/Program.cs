using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleTasks.Cli;
using TeleTasks.Configuration;
using TeleTasks.Services;

if (args.Length > 0 && args[0].Equals("discover", StringComparison.OrdinalIgnoreCase))
{
    using var dcts = new CancellationTokenSource();
    ConsoleCancelEventHandler dh = (_, e) => { e.Cancel = true; dcts.Cancel(); };
    Console.CancelKeyPress += dh;
    try { return await DiscoverCommand.RunAsync(args.Skip(1).ToArray(), dcts.Token); }
    finally { Console.CancelKeyPress -= dh; }
}

if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    using var scts = new CancellationTokenSource();
    ConsoleCancelEventHandler sh = (_, e) => { e.Cancel = true; scts.Cancel(); };
    Console.CancelKeyPress += sh;
    try { return await SetupCommand.RunAsync(args.Skip(1).ToArray(), scts.Token); }
    finally { Console.CancelKeyPress -= sh; }
}

if (args.Length > 0 && args[0].Equals("where", StringComparison.OrdinalIgnoreCase))
{
    return WhereCommand.Run(args.Skip(1).ToArray());
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Host.CreateApplicationBuilder already loads appsettings.json, the env-specific
// file, DOTNET_ env vars, and the command line. We layer on:
//   - legacy appsettings.Local.json next to the binary (older installs)
//   - appsettings.Local.json in the user's config dir (canonical location)
//   - TELETASKS_-prefixed env vars (deployment override)
//   - command line (re-applied last so it always wins)
//
// The user-config-dir copy comes last among the JSON files so it overrides
// anything in the bin output, regardless of Debug/Release configuration.
var configDir = TeleTasks.Configuration.UserConfigDirectory.Resolve();
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(configDir, "appsettings.Local.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "TELETASKS_")
    .AddCommandLine(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:Token"]))
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine(
            "Telegram:Token not configured and stdin/stdout are not a terminal.");
        Console.Error.WriteLine(
            "Run `dotnet run -- setup` once interactively, or set TELETASKS_Telegram__Token.");
        return 1;
    }

    using var setupCts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; setupCts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        var savePath = SetupCommand.DefaultSavePath;
        var ok = await SetupCommand.RunInteractiveAsync(savePath, setupCts.Token);
        if (!ok) return 1;
    }
    finally
    {
        // Must unsubscribe before `using var setupCts` disposes the CTS — otherwise
        // a later Ctrl+C (during host.RunAsync) would call Cancel on a disposed
        // CancellationTokenSource and throw ObjectDisposedException on the SIGINT thread.
        Console.CancelKeyPress -= onCancel;
    }

    ((IConfigurationRoot)builder.Configuration).Reload();
}

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<TaskCatalogOptions>(builder.Configuration.GetSection(TaskCatalogOptions.SectionName));

builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddSingleton<OutputCollector>();
builder.Services.AddSingleton<JobTracker>();
builder.Services.AddSingleton<TaskExecutor>();
builder.Services.AddSingleton<TaskMatcher>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddHttpClient(OllamaClient.HttpClientName);

builder.Services.AddHostedService<TelegramBotService>();

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();

// Log every active configuration source so a "why is my Local.json being ignored?"
// problem is always one logline away from being answered.
{
    var startupLogger = host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("TeleTasks.Configuration");
    startupLogger.LogInformation("Config dir: {Dir}", configDir);
    foreach (var src in ((Microsoft.Extensions.Configuration.IConfigurationRoot)builder.Configuration).Providers)
    {
        if (src is Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider j)
        {
            var path = j.Source.Path ?? "(unknown)";
            var fullPath = j.Source.FileProvider is Microsoft.Extensions.FileProviders.PhysicalFileProvider pfp
                ? Path.Combine(pfp.Root, path)
                : path;
            var exists = File.Exists(fullPath);
            startupLogger.LogInformation(
                "  json {Path} {Status}",
                fullPath, exists ? "(loaded)" : "(missing, optional)");
        }
        else
        {
            startupLogger.LogInformation("  {Type}", src.GetType().Name);
        }
    }
}

await host.RunAsync();
return 0;
