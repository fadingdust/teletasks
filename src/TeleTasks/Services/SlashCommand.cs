namespace TeleTasks.Services;

/// <summary>
/// Recognises whether a user message is a slash command (<c>/help</c>,
/// <c>/job 5</c>, <c>/help@MyBot</c>) versus an absolute filesystem path
/// that happens to start with <c>/</c> (<c>/var/log/syslog</c>,
/// <c>/home/user/file.png</c>).
///
/// Real slash commands are <c>/&lt;name&gt;[@&lt;botname&gt;]</c> where
/// <c>&lt;name&gt;</c> is letters / digits / underscore and contains no
/// further <c>/</c>. The optional <c>@&lt;botname&gt;</c> suffix is what
/// Telegram appends in group chats so multi-bot conversations can be
/// disambiguated. Paths fail the no-embedded-slash check and the bot
/// routes them through the conversational-parameter loop or the matcher
/// instead.
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
        var i = 2;
        for (; i < end; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                // The only non-word character allowed in the verb token is a
                // single '@' separating the command from the bot mention.
                // Anything past the '@' must be word-shaped too.
                if (c != '@') return false;
                if (i == 2) return false;       // "/@something" is not a command
                for (var j = i + 1; j < end; j++)
                {
                    var bc = text[j];
                    if (!char.IsLetterOrDigit(bc) && bc != '_') return false;
                }
                if (i + 1 == end) return false; // bare '@' with no bot name
                return true;
            }
        }
        return true;
    }
}
