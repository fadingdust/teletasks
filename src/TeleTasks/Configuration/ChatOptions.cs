using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeleTasks.Configuration;

/// <summary>
/// Cross-provider chat settings — knobs that aren't specific to any one
/// backend (Telegram / Discord / future). Per-provider config (tokens,
/// allow-lists) lives under <c>Chat:Providers:&lt;Name&gt;:*</c> in
/// matching options classes (e.g. <c>TelegramProviderOptions</c>).
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>
    /// How often the job notifier polls active long-running jobs to push
    /// new artifacts / completion summaries. <c>0</c> disables progressive
    /// notifications entirely.
    /// </summary>
    public int JobPollSeconds { get; set; } = 30;

    /// <summary>
    /// When true, the bot DMs the provider's <c>DefaultRecipient</c> at
    /// startup if the Ollama health check fails (unreachable, or
    /// configured model not pulled).
    /// </summary>
    public bool StartupNotificationsEnabled { get; set; } = true;
}

/// <summary>
/// Bridge from legacy <c>Telegram:*</c> keys to the new <c>Chat:*</c>
/// section. Runs after the <c>Chat:</c> binding does its work; if a
/// <c>Chat:</c> key wasn't present in <see cref="IConfiguration"/>, copies
/// the matching value from <see cref="TelegramOptions"/> and logs a
/// one-shot deprecation hint per field. Removed when the legacy
/// <c>Telegram:*</c> section is dropped (post-step-3, after the
/// deprecation period closes).
/// </summary>
public sealed class ChatOptionsDefaults : IConfigureOptions<ChatOptions>
{
    private readonly IConfiguration _config;
    private readonly TelegramOptions _legacy;
    private readonly ILogger<ChatOptionsDefaults> _logger;

    public ChatOptionsDefaults(
        IConfiguration config,
        IOptions<TelegramOptions> legacy,
        ILogger<ChatOptionsDefaults> logger)
    {
        _config = config;
        _legacy = legacy.Value;
        _logger = logger;
    }

    public void Configure(ChatOptions options)
    {
        if (_config["Chat:JobPollSeconds"] is null)
        {
            if (_config["Telegram:JobPollSeconds"] is not null)
            {
                _logger.LogWarning(
                    "Telegram:JobPollSeconds is deprecated. Move it to Chat:JobPollSeconds.");
            }
            options.JobPollSeconds = _legacy.JobPollSeconds;
        }

        if (_config["Chat:StartupNotificationsEnabled"] is null)
        {
            if (_config["Telegram:StartupNotificationsEnabled"] is not null)
            {
                _logger.LogWarning(
                    "Telegram:StartupNotificationsEnabled is deprecated. Move it to Chat:StartupNotificationsEnabled.");
            }
            options.StartupNotificationsEnabled = _legacy.StartupNotificationsEnabled;
        }
    }
}
