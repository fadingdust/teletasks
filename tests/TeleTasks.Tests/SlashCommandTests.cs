using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public class SlashCommandTests
{
    [Theory]
    [InlineData("/help")]
    [InlineData("/tasks")]
    [InlineData("/job 5")]
    [InlineData("/stop 12")]
    [InlineData("/dry tail the syslog")]
    [InlineData("/results sh_render_loop")]
    [InlineData("/whoami")]
    [InlineData("/cancel")]
    [InlineData("/job_5")]              // underscore allowed in verb
    [InlineData("/abc123")]             // digits after the leading letter
    public void Real_slash_commands_are_recognised(string text)
    {
        Assert.True(SlashCommand.IsCommand(text));
    }

    [Theory]
    [InlineData("/var/log/syslog")]
    [InlineData("/home/me/Projects/render-loop/output")]
    [InlineData("/etc/passwd")]
    [InlineData("/tmp/file.png")]
    [InlineData("/proc/12345/stat")]
    [InlineData("/")]                   // bare slash
    [InlineData("/ ")]                  // slash + space, no verb
    [InlineData("//double")]            // double-slash anomaly
    public void Paths_and_pathlike_strings_are_not_recognised(string text)
    {
        Assert.False(SlashCommand.IsCommand(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("hello")]               // doesn't start with /
    [InlineData("tail the syslog")]     // natural language
    [InlineData(" /help")]              // leading space — not a command
    public void Non_slash_inputs_are_not_recognised(string? text)
    {
        Assert.False(SlashCommand.IsCommand(text));
    }

    [Theory]
    [InlineData("/123")]                // first char of verb must be a letter
    [InlineData("/-help")]              // first char of verb must be a letter
    [InlineData("/help-me")]            // hyphen in verb not allowed
    [InlineData("/help.me")]            // dot in verb not allowed (file-extension-shaped)
    public void Bot_command_grammar_rejects_non_word_verb_chars(string text)
    {
        Assert.False(SlashCommand.IsCommand(text));
    }

    [Fact]
    public void Verb_is_terminated_by_whitespace_only()
    {
        // "/job 5" matches; "/job/5" does not (path-like, embedded slash).
        Assert.True(SlashCommand.IsCommand("/job 5"));
        Assert.False(SlashCommand.IsCommand("/job/5"));
    }
}
