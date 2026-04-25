using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;

namespace TeleTasks.Services;

public sealed class OllamaClient
{
    public const string HttpClientName = "Ollama";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _factory;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(IHttpClientFactory factory, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<string> ChatJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
        ChatAsync(systemPrompt, userPrompt, format: "json", cancellationToken);

    public Task<string> ChatStructuredAsync(string systemPrompt, string userPrompt, JsonNode schema, CancellationToken cancellationToken) =>
        ChatAsync(systemPrompt, userPrompt, format: schema, cancellationToken);

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt, object? format, CancellationToken cancellationToken)
    {
        using var http = _factory.CreateClient(HttpClientName);
        http.BaseAddress ??= new Uri(_options.Endpoint.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        var request = new ChatRequest
        {
            Model = _options.Model,
            Stream = false,
            Format = format,
            Options = new ChatOptions { Temperature = _options.Temperature },
            Messages = new List<ChatMessage>
            {
                new("system", systemPrompt),
                new("user", userPrompt)
            }
        };

        using var response = await http.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Ollama returned {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException(
                $"Ollama responded with HTTP {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Empty response from Ollama.");

        return payload.Message?.Content ?? string.Empty;
    }

    private sealed class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public object? Format { get; set; }
        public bool Stream { get; set; }
        public ChatOptions? Options { get; set; }
    }

    private sealed record ChatMessage(string Role, string Content);

    private sealed class ChatOptions
    {
        public double Temperature { get; set; }
    }

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
        public bool Done { get; set; }
    }
}
