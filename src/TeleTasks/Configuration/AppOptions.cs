namespace TeleTasks.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string Token { get; set; } = string.Empty;

    public long[] AllowedUserIds { get; set; } = Array.Empty<long>();

    public long[] AllowedChatIds { get; set; } = Array.Empty<long>();
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "llama3.2:1b";

    public double Temperature { get; set; } = 0.0;

    public int RequestTimeoutSeconds { get; set; } = 60;
}

public sealed class TaskCatalogOptions
{
    public const string SectionName = "TaskCatalog";

    public string Path { get; set; } = "tasks.json";

    public int CommandTimeoutSeconds { get; set; } = 60;

    public string WorkingDirectory { get; set; } = string.Empty;
}
