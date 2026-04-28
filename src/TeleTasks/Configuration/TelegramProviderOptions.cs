using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeleTasks.Configuration;

/// <summary>
/// Per-provider Telegram config under <c>Chat:Providers:Telegram:*</c>.
/// Mirrors the legacy <c>Telegram:*</c> section that's still read for one
/// release as a backward-compat fallback (see
/// <see cref="TelegramProviderOptionsDefaults"/>). Discord adds a parallel
/// <c>Chat:Providers:Discord:*</c> section in step 3.
/// </summary>
public sealed class TelegramProviderOptions
{
    public const string SectionName = "Chat:Providers:Telegram";

    public string Token { get; set; } = string.Empty;
    public long[] AllowedUserIds { get; set; } = Array.Empty<long>();
    public long[] AllowedChatIds { get; set; } = Array.Empty<long>();
}

/// <summary>
/// Same legacy-fallback pattern as <c>ChatOptionsDefaults</c>: when a
/// <c>Chat:Providers:Telegram:*</c> key isn't set, copy the matching
/// <see cref="TelegramOptions"/> field and log a one-shot deprecation
/// hint. Dropped after the deprecation period closes.
/// </summary>
public sealed class TelegramProviderOptionsDefaults : IConfigureOptions<TelegramProviderOptions>
{
    private readonly IConfiguration _config;
    private readonly TelegramOptions _legacy;
    private readonly ILogger<TelegramProviderOptionsDefaults> _logger;

    public TelegramProviderOptionsDefaults(
        IConfiguration config,
        IOptions<TelegramOptions> legacy,
        ILogger<TelegramProviderOptionsDefaults> logger)
    {
        _config = config;
        _legacy = legacy.Value;
        _logger = logger;
    }

    public void Configure(TelegramProviderOptions options)
    {
        if (string.IsNullOrEmpty(_config["Chat:Providers:Telegram:Token"]))
        {
            if (!string.IsNullOrEmpty(_config["Telegram:Token"]))
            {
                _logger.LogWarning(
                    "Telegram:Token is deprecated. Move it to Chat:Providers:Telegram:Token.");
            }
            options.Token = _legacy.Token;
        }

        // Array binding: presence is detected via the indexed children Chat:Providers:Telegram:AllowedUserIds:0.
        if (_config.GetSection("Chat:Providers:Telegram:AllowedUserIds").GetChildren().Any() == false)
        {
            if (_config.GetSection("Telegram:AllowedUserIds").GetChildren().Any())
            {
                _logger.LogWarning(
                    "Telegram:AllowedUserIds is deprecated. Move it to Chat:Providers:Telegram:AllowedUserIds.");
            }
            options.AllowedUserIds = _legacy.AllowedUserIds;
        }

        if (_config.GetSection("Chat:Providers:Telegram:AllowedChatIds").GetChildren().Any() == false)
        {
            if (_config.GetSection("Telegram:AllowedChatIds").GetChildren().Any())
            {
                _logger.LogWarning(
                    "Telegram:AllowedChatIds is deprecated. Move it to Chat:Providers:Telegram:AllowedChatIds.");
            }
            options.AllowedChatIds = _legacy.AllowedChatIds;
        }
    }
}
