using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleTasks.Configuration;
using TeleTasks.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "TELETASKS_")
    .AddCommandLine(args);

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<TaskCatalogOptions>(builder.Configuration.GetSection(TaskCatalogOptions.SectionName));

builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddSingleton<OutputCollector>();
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
await host.RunAsync();
