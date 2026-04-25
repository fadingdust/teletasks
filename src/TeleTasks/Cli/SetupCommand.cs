using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleTasks.Configuration;

namespace TeleTasks.Cli;

public static class SetupCommand
{
    private const string TelegramApi = "https://api.telegram.org";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultSavePath => UserConfigDirectory.LocalSettingsPath;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var savePath = DefaultSavePath;
        UserConfigDirectory.EnsureExists();
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length)
            {
                savePath = args[++i];
            }
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("teletasks setup needs an interactive terminal (stdin is redirected).");
            return 1;
        }

        return await RunInteractiveAsync(savePath, cancellationToken) ? 0 : 1;
    }

    public static async Task<bool> RunInteractiveAsync(string savePath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Console.WriteLine();
        Console.WriteLine("TeleTasks setup");
        Console.WriteLine("===============");
        Console.WriteLine();
        Console.WriteLine("This wizard will configure Telegram + Ollama and save the result to:");
        Console.WriteLine($"  {savePath}");
        Console.WriteLine();
        Console.WriteLine("That file is loaded on top of appsettings.json so secrets stay out of git.");
        Console.WriteLine("It's already covered by .gitignore.");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        var (token, botUsername) = await PromptTokenAsync(http, cancellationToken);
        if (token is null) return false;

        var userId = await PromptUserIdAsync(http, token, botUsername!, cancellationToken);
        if (userId is null) return false;

        var (endpoint, model) = await PromptOllamaAsync(http, cancellationToken);

        var existing = TryLoadJson(savePath);
        var output = MergeIntoLocalConfig(existing, token, userId.Value, endpoint, model);
        File.WriteAllText(savePath, output.ToJsonString(WriteOptions));

        Console.WriteLine();
        Console.WriteLine($"Saved configuration to {savePath}.");
        Console.WriteLine("Re-run this wizard any time with `dotnet run -- setup`.");
        Console.WriteLine();
        return true;
    }

    private static async Task<(string? token, string? botUsername)> PromptTokenAsync(HttpClient http, CancellationToken ct)
    {
        while (true)
        {
            Console.Write("Telegram bot token (from @BotFather): ");
            var token = (Console.ReadLine() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(token)) return (null, null);

            try
            {
                var info = await http.GetFromJsonAsync<JsonElement>(
                    $"{TelegramApi}/bot{token}/getMe", ct);
                if (info.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                    info.TryGetProperty("result", out var result))
                {
                    var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var name = result.TryGetProperty("first_name", out var n) ? n.GetString() : null;
                    Console.WriteLine($"  ✓ @{username} ({name})");
                    return (token, username);
                }
                Console.WriteLine("  ✗ Telegram rejected the token. Try again.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Could not reach Telegram: {ex.Message}");
                Console.Write("  Retry? [Y/n]: ");
                var resp = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
                if (resp is "n" or "no") return (null, null);
            }
        }
    }

    private static async Task<long?> PromptUserIdAsync(HttpClient http, string token, string botUsername, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Open Telegram and send ANY message to @{botUsername}.");
        Console.WriteLine("(Waiting up to 5 minutes — Ctrl+C to cancel.)");

        try
        {
            await http.GetAsync($"{TelegramApi}/bot{token}/deleteWebhook", ct);
        }
        catch { }

        var deadline = DateTime.UtcNow.AddMinutes(5);
        long offset = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JsonElement payload;
            try
            {
                payload = await http.GetFromJsonAsync<JsonElement>(
                    $"{TelegramApi}/bot{token}/getUpdates?timeout=30&offset={offset}", ct);
            }
            catch (TaskCanceledException) { continue; }
            catch (Exception ex)
            {
                Console.WriteLine($"  warn: getUpdates failed: {ex.Message}");
                await Task.Delay(2000, ct);
                continue;
            }

            if (!payload.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array) continue;

            foreach (var update in result.EnumerateArray())
            {
                if (update.TryGetProperty("update_id", out var uid))
                {
                    offset = Math.Max(offset, uid.GetInt64() + 1);
                }

                JsonElement message;
                if (update.TryGetProperty("message", out var m1)) message = m1;
                else if (update.TryGetProperty("edited_message", out var m2)) message = m2;
                else continue;

                if (!message.TryGetProperty("from", out var from)) continue;
                if (!from.TryGetProperty("id", out var idEl)) continue;

                var userId = idEl.GetInt64();
                var username = from.TryGetProperty("username", out var u) ? u.GetString() : null;
                var first = from.TryGetProperty("first_name", out var f) ? f.GetString() : null;
                var label = !string.IsNullOrEmpty(username) ? $"@{username}" : first ?? "(no username)";

                Console.WriteLine();
                Console.Write($"  Got a message from {label} (user ID {userId}). Allow this user? [Y/n]: ");
                var resp = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
                if (resp is "" or "y" or "yes") return userId;
                Console.WriteLine("  Skipped. Send another message and try again.");
            }
        }

        Console.Error.WriteLine("  ✗ Timed out waiting for a message.");
        return null;
    }

    private static async Task<(string endpoint, string model)> PromptOllamaAsync(HttpClient http, CancellationToken ct)
    {
        Console.WriteLine();
        Console.Write("Ollama endpoint [http://localhost:11434]: ");
        var endpoint = (Console.ReadLine() ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(endpoint)) endpoint = "http://localhost:11434";

        var available = await ListOllamaModelsAsync(http, endpoint, ct);
        if (available.Count == 0)
        {
            Console.WriteLine("  warn: could not query Ollama (it may not be running). Continuing.");
        }
        else
        {
            Console.WriteLine($"  Available models: {string.Join(", ", available.Take(8))}{(available.Count > 8 ? ", ..." : "")}");
        }

        Console.Write("Ollama model [llama3.2:1b]: ");
        var model = (Console.ReadLine() ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(model)) model = "llama3.2:1b";

        if (available.Count > 0 && !available.Contains(model))
        {
            Console.WriteLine($"  warn: '{model}' is not pulled. Run `ollama pull {model}` before starting the bot.");
        }

        return (endpoint, model);
    }

    private static async Task<List<string>> ListOllamaModelsAsync(HttpClient http, string endpoint, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var doc = await http.GetFromJsonAsync<JsonElement>(
                endpoint.TrimEnd('/') + "/api/tags", cts.Token);
            if (!doc.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array) return new List<string>();
            return models.EnumerateArray()
                .Where(m => m.TryGetProperty("name", out _))
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static JsonObject? TryLoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
        catch (JsonException) { return null; }
    }

    private static JsonObject MergeIntoLocalConfig(JsonObject? existing, string token, long userId, string endpoint, string model)
    {
        var root = existing ?? new JsonObject();

        var telegram = (root["Telegram"] as JsonObject) ?? new JsonObject();
        telegram["Token"] = token;

        var allowedUsers = (telegram["AllowedUserIds"] as JsonArray) ?? new JsonArray();
        if (!allowedUsers.Any(n => n is JsonValue v && v.TryGetValue<long>(out var existingId) && existingId == userId))
        {
            allowedUsers.Add(userId);
        }
        telegram["AllowedUserIds"] = allowedUsers;
        if (telegram["AllowedChatIds"] is null) telegram["AllowedChatIds"] = new JsonArray();
        root["Telegram"] = telegram;

        var ollama = (root["Ollama"] as JsonObject) ?? new JsonObject();
        ollama["Endpoint"] = endpoint;
        ollama["Model"] = model;
        root["Ollama"] = ollama;

        return root;
    }
}
