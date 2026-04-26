using TeleTasks.Models;
using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public class ParameterValueParserTests
{
    private static TaskParameter Param(string type, List<string>? choices = null) => new()
    {
        Name = "x",
        Type = type,
        Required = false,
        Enum = choices
    };

    [Theory]
    [InlineData("42", 42L)]
    [InlineData(" -7 ", -7L)]
    [InlineData("0", 0L)]
    public void TryParse_integer_accepts_whole_numbers(string input, long expected)
    {
        Assert.True(ParameterValueParser.TryParse(Param("integer"), input, out var value, out var error));
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("3.14")]      // floats aren't integers
    [InlineData("")]
    public void TryParse_integer_rejects_non_integers(string input)
    {
        Assert.False(ParameterValueParser.TryParse(Param("integer"), input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Contains("integer", error);
    }

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("-1.5", -1.5)]
    [InlineData("0", 0.0)]
    public void TryParse_number_accepts_floats_invariant_culture(string input, double expected)
    {
        // Invariant culture: 1.5 is valid, 1,5 (some locales) is not.
        Assert.True(ParameterValueParser.TryParse(Param("number"), input, out var value, out _));
        Assert.Equal(expected, (double)value!, precision: 5);
    }

    [Fact]
    public void TryParse_number_rejects_comma_decimal()
    {
        Assert.False(ParameterValueParser.TryParse(Param("number"), "1,5", out _, out var error));
        Assert.Contains("number", error!);
    }

    [Theory]
    [InlineData("y",     true)]
    [InlineData("yes",   true)]
    [InlineData("YES",   true)]
    [InlineData("true",  true)]
    [InlineData("1",     true)]
    [InlineData("on",    true)]
    [InlineData("n",     false)]
    [InlineData("no",    false)]
    [InlineData("false", false)]
    [InlineData("0",     false)]
    [InlineData("off",   false)]
    public void TryParse_boolean_accepts_canonical_forms(string input, bool expected)
    {
        Assert.True(ParameterValueParser.TryParse(Param("boolean"), input, out var value, out _));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("definitely")]
    [InlineData("")]
    public void TryParse_boolean_rejects_other_strings(string input)
    {
        Assert.False(ParameterValueParser.TryParse(Param("boolean"), input, out _, out var error));
        Assert.Contains("yes/no", error!);
    }

    [Fact]
    public void TryParse_string_returns_trimmed_value()
    {
        Assert.True(ParameterValueParser.TryParse(Param("string"), "  hello  ", out var value, out _));
        Assert.Equal("hello", value);
    }

    [Fact]
    public void TryParse_enum_accepts_case_insensitive_match_and_returns_canonical()
    {
        var p = Param("string", new List<string> { "Sunny", "Cloudy", "Rainy" });
        Assert.True(ParameterValueParser.TryParse(p, "cloudy", out var value, out _));
        Assert.Equal("Cloudy", value);   // canonical form, not the user's casing
    }

    [Fact]
    public void TryParse_enum_rejects_value_not_in_list_with_helpful_error()
    {
        var p = Param("string", new List<string> { "low", "medium", "high" });
        Assert.False(ParameterValueParser.TryParse(p, "extreme", out _, out var error));
        Assert.Contains("low", error!);
        Assert.Contains("medium", error);
        Assert.Contains("high", error);
    }

    [Fact]
    public void TryParse_truncates_very_long_bad_input_in_error()
    {
        var huge = new string('x', 200);
        Assert.False(ParameterValueParser.TryParse(Param("integer"), huge, out _, out var error));
        // Sanity bound on error length so a multi-MB user-supplied blob can't bloat the bot reply.
        Assert.True(error!.Length < 100);
    }
}
