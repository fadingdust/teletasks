using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeleTasks.Services.Chat;

/// <summary>
/// Provider-qualified chat identifier. The provider segment lets the
/// notifier loop, conversation state, and persisted job records share
/// one address space across multiple chat backends without colliding.
///
/// Canonical wire format is <c>provider:id</c> (<c>"telegram:42"</c>,
/// <c>"discord:1234567890"</c>). The <c>Id</c> portion is whatever the
/// provider considers a chat identity — Telegram's <c>chat.Id</c>,
/// Discord's channel ID, Matrix's room ID. We keep it as a string so
/// providers with non-numeric IDs (Matrix: <c>!abc:matrix.org</c>) just
/// work.
/// </summary>
[JsonConverter(typeof(ChatIdJsonConverter))]
public readonly record struct ChatId(string Provider, string Id)
{
    public override string ToString() => $"{Provider}:{Id}";

    public static ChatId Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) throw new FormatException("Empty ChatId.");
        var idx = s.IndexOf(':');
        if (idx <= 0 || idx == s.Length - 1)
            throw new FormatException($"ChatId must be 'provider:id', got '{s}'.");
        return new ChatId(s[..idx], s[(idx + 1)..]);
    }

    public static bool TryParse(string? s, out ChatId result)
    {
        result = default;
        if (string.IsNullOrEmpty(s)) return false;
        var idx = s.IndexOf(':');
        if (idx <= 0 || idx == s.Length - 1) return false;
        result = new ChatId(s[..idx], s[(idx + 1)..]);
        return true;
    }

    /// <summary>
    /// Bridge for Telegram's long chat IDs at the source — call sites
    /// that already have a long can keep their ergonomics.
    /// </summary>
    public static ChatId FromTelegram(long chatId) => new("telegram", chatId.ToString());
}

/// <summary>
/// Reads the legacy <see cref="long"/> form (Telegram-only era) and the
/// new <c>provider:id</c> string form. Always writes the new form.
/// Lets existing <c>jobs.json</c> files survive the upgrade — a job
/// record with <c>"chatId": 12345</c> deserialises to
/// <c>ChatId("telegram", "12345")</c>.
/// </summary>
internal sealed class ChatIdJsonConverter : JsonConverter<ChatId>
{
    public override ChatId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var s = reader.GetString();
                return ChatId.Parse(s ?? string.Empty);
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return ChatId.FromTelegram(l);
                throw new JsonException("Numeric ChatId out of int64 range.");
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for ChatId.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ChatId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
