namespace TeleTasks.Services;

/// <summary>
/// Recognises whether a user message is a slash command (<c>/help</c>,
/// <c>/job 5</c>) versus an absolute filesystem path that happens to start
/// with <c>/</c> (<c>/var/log/syslog</c>, <c>/home/user/file.png</c>).
///
/// Real slash commands are <c>/&lt;name&gt;</c> where <c>&lt;name&gt;</c>
/// is letters / digits / underscore and contains no further <c>/</c>.
/// Paths fail the no-embedded-slash check and the bot routes them through
/// the conversational-parameter loop or the matcher instead.
/// </summary>
public static class SlashCommand
{
    public static bool IsCommand(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] != '/') return false;

        // Walk to the end of the leading token (up to first whitespace).
        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (end == 1) return false;             // bare "/" or "/<space>"

        // Verb shape: letter, then letters/digits/underscore. Anything with
        // an embedded '/' (paths) or '.' or other separators is not a command.
        if (!char.IsLetter(text[1])) return false;
        for (var i = 2; i < end; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }
}
