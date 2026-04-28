using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Services.Chat;

namespace TeleTasks.Services;

/// <summary>
/// Lifecycle host for the active <see cref="IChatProvider"/>. Owns provider
/// start/stop, wires the <see cref="MessageRouter"/> to the provider's
/// <c>OnMessage</c> event, and sends the Ollama startup health notification.
/// All message routing logic lives in <see cref="MessageRouter"/>.
/// </summary>
public sealed class ChatHost : BackgroundService
{
    private readonly IChatProvider _provider;
    private readonly MessageRouter _router;
    private readonly ChatOptions _chatOptions;
    private readonly OllamaClient _ollama;
    private readonly ILogger<ChatHost> _logger;

    public ChatHost(
        IChatProvider provider,
        MessageRouter router,
        IOptions<ChatOptions> chatOptions,
        OllamaClient ollama,
        ILogger<ChatHost> logger)
    {
        _provider = provider;
        _router = router;
        _chatOptions = chatOptions.Value;
        _ollama = ollama;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _provider.OnMessage += _router.HandleAsync;
        await _provider.StartAsync(stoppingToken);
        _logger.LogInformation("Chat provider started.");

        await CheckOllamaHealthAndNotifyAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task CheckOllamaHealthAndNotifyAsync(CancellationToken cancellationToken)
    {
        string? warning = null;
        try
        {
            var models = await _ollama.ListModelsAsync(cancellationToken);
            if (models.Count == 0)
            {
                warning =
                    $"I'm online, but Ollama at <code>{MessageRouter.HtmlEscape(_ollama.ConfiguredEndpoint)}</code> " +
                    "reports no installed models.\n\n" +
                    $"On the host machine run:\n<pre>ollama pull {MessageRouter.HtmlEscape(_ollama.ConfiguredModel)}</pre>";
                _logger.LogWarning("Ollama is reachable but has no models pulled.");
            }
            else if (!models.Contains(_ollama.ConfiguredModel, StringComparer.OrdinalIgnoreCase))
            {
                warning =
                    $"I'm online, but Ollama doesn't have model <code>{MessageRouter.HtmlEscape(_ollama.ConfiguredModel)}</code> pulled.\n\n" +
                    $"Available: <code>{MessageRouter.HtmlEscape(string.Join(", ", models.Take(8)))}</code>\n\n" +
                    $"On the host machine run:\n<pre>ollama pull {MessageRouter.HtmlEscape(_ollama.ConfiguredModel)}</pre>";
                _logger.LogWarning("Configured Ollama model '{Model}' is not pulled. Available: {Models}",
                    _ollama.ConfiguredModel, string.Join(", ", models));
            }
            else
            {
                _logger.LogInformation("Ollama health: ok ({Model} pulled, {Count} model(s) available).",
                    _ollama.ConfiguredModel, models.Count);
            }
        }
        catch (OllamaUnreachableException ex)
        {
            warning =
                $"I'm online, but I can't reach Ollama at <code>{MessageRouter.HtmlEscape(_ollama.ConfiguredEndpoint)}</code>.\n\n" +
                $"<pre>{MessageRouter.HtmlEscape(ex.Message)}</pre>\n\n" +
                "Start it with:\n<pre>ollama serve</pre>";
            _logger.LogWarning(ex, "Ollama is unreachable at startup.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama health check failed unexpectedly.");
        }

        if (warning is not null)
        {
            await SendStartupNotificationAsync(warning, cancellationToken);
        }
    }

    private async Task SendStartupNotificationAsync(string htmlBody, CancellationToken cancellationToken)
    {
        if (!_chatOptions.StartupNotificationsEnabled) return;

        var recipient = _provider.DefaultRecipient;
        if (recipient is null)
        {
            _logger.LogWarning("Startup notification not sent: provider has no default recipient.");
            return;
        }

        try
        {
            await _provider.SendHtmlAsync(recipient.Value, htmlBody, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send startup notification to {Recipient}", recipient);
        }
    }
}
