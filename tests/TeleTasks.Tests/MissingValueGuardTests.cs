using TeleTasks.Models;
using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public class MissingValueGuardTests
{
    private static TaskParameter Param(string type = "string", string name = "x") => new()
    {
        Name = name,
        Type = type,
        Required = true
    };

    private static IReadOnlyDictionary<string, object?> Values(params (string k, object? v)[] kvs) =>
        kvs.ToDictionary(t => t.k, t => t.v);

    [Fact]
    public void HasUsableValue_returns_false_when_key_missing()
    {
        Assert.False(MissingValueGuard.HasUsableValue(Param(), Values(), userMessage: "anything"));
    }

    [Fact]
    public void HasUsableValue_returns_false_when_value_is_null()
    {
        Assert.False(MissingValueGuard.HasUsableValue(Param(), Values(("x", null)), userMessage: "tail the syslog"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void HasUsableValue_returns_false_for_blank_string_values(string blank)
    {
        Assert.False(MissingValueGuard.HasUsableValue(Param(), Values(("x", blank)), userMessage: "anything"));
    }

    [Fact]
    public void HasUsableValue_trusts_integer_values_without_consulting_message()
    {
        // 5 isn't a substring of "tail" but numbers / bools / enums skip the
        // hallucination guard — the schema constrains them enough that the
        // model can't pick truly out-of-band values.
        Assert.True(MissingValueGuard.HasUsableValue(Param("integer"), Values(("x", 5L)), userMessage: "tail"));
    }

    [Fact]
    public void HasUsableValue_trusts_boolean_values_without_consulting_message()
    {
        Assert.True(MissingValueGuard.HasUsableValue(Param("boolean"), Values(("x", true)), userMessage: "anything"));
    }

    [Fact]
    public void HasUsableValue_trusts_string_when_value_token_appears_in_message()
    {
        var values = Values(("path", "/var/log/syslog"));
        var p = Param(name: "path");
        // Paraphrased: user said "syslog", LLM expanded to /var/log/syslog.
        Assert.True(MissingValueGuard.HasUsableValue(p, values, userMessage: "tail the syslog file"));
    }

    [Fact]
    public void HasUsableValue_rejects_string_when_no_token_appears_in_message()
    {
        // Classic hallucination: user typed nothing about run.sh but LLM emitted it.
        var values = Values(("arg1", "run.sh"));
        var p = Param(name: "arg1");
        Assert.False(MissingValueGuard.HasUsableValue(p, values, userMessage: "I want to do something else"));
    }

    [Fact]
    public void HasUsableValue_strips_task_name_before_checking_tokens()
    {
        // Reproducer for the false-positive case: user typed only the task
        // name "sh_render_loop", LLM hallucinated arg1="render.sh". Without
        // stripping, the guard would trust "render.sh" because the token
        // "render" appears in the user's message — but only because the
        // user's message IS the task name. Stripping the task name leaves
        // an empty residual, so the value is correctly classified missing.
        var values = Values(("arg1", "render.sh"));
        var p = Param(name: "arg1");
        Assert.False(MissingValueGuard.HasUsableValue(p, values,
            userMessage: "sh_render_loop",
            taskName: "sh_render_loop"));
    }

    [Fact]
    public void HasUsableValue_accepts_string_when_user_message_includes_value_with_separators()
    {
        var values = Values(("arg1", "render.sh"));
        var p = Param(name: "arg1");
        Assert.True(MissingValueGuard.HasUsableValue(p, values,
            userMessage: "run sh_render_loop with render.sh and 0",
            taskName: "sh_render_loop"));
    }

    [Fact]
    public void HasUsableValue_trusts_short_values_that_cant_be_meaningfully_tokenized()
    {
        // "5" tokenized → no >=3-char tokens → trust by default rather than
        // demanding it appear verbatim. Single short literals are typical
        // for counts / seeds / ids and rarely hallucinated.
        var values = Values(("x", "5"));
        Assert.True(MissingValueGuard.HasUsableValue(Param(), values,
            userMessage: "completely unrelated text"));
    }

    [Fact]
    public void HasUsableValue_skips_message_check_when_user_text_is_empty()
    {
        // No user text at all → treat the value as good if it's non-blank.
        // Programmatic callers that don't carry a message shouldn't be
        // punished for it.
        Assert.True(MissingValueGuard.HasUsableValue(Param(), Values(("x", "anything")), userMessage: ""));
    }

    [Fact]
    public void HasUsableValue_is_case_insensitive_on_substring_match()
    {
        var values = Values(("path", "/Var/Log/Syslog"));
        Assert.True(MissingValueGuard.HasUsableValue(Param(name: "path"), values, userMessage: "tail SYSLOG"));
    }

    [Fact]
    public void HasUsableValue_treats_only_task_name_as_no_user_text()
    {
        // After stripping "tail_log" from "tail_log", searchText is just
        // whitespace → every required string becomes missing.
        var values = Values(("path", "/var/log/anything"));
        Assert.False(MissingValueGuard.HasUsableValue(Param(name: "path"), values,
            userMessage: "tail_log",
            taskName: "tail_log"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    public void HasUsableValue_trusts_two_char_values_too(string shortValue)
    {
        Assert.True(MissingValueGuard.HasUsableValue(Param(),
            Values(("x", shortValue)),
            userMessage: "totally unrelated"));
    }
}
