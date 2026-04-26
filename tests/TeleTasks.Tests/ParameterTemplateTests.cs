using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public class ParameterTemplateTests
{
    [Fact]
    public void Apply_substitutes_a_single_placeholder()
    {
        var values = new Dictionary<string, object?> { ["name"] = "world" };
        Assert.Equal("hello world", ParameterTemplate.Apply("hello {name}", values));
    }

    [Fact]
    public void Apply_leaves_unknown_placeholders_alone()
    {
        var values = new Dictionary<string, object?> { ["a"] = "1" };
        Assert.Equal("1 {b}", ParameterTemplate.Apply("{a} {b}", values));
    }

    [Fact]
    public void Apply_handles_null_or_empty_template_unchanged()
    {
        var values = new Dictionary<string, object?>();
        Assert.Equal(string.Empty, ParameterTemplate.Apply(string.Empty, values));
        Assert.Null(ParameterTemplate.Apply(null!, values));
    }

    [Fact]
    public void Apply_resolves_nested_references_in_a_second_pass()
    {
        // output_dir's default references {lora}; the caller passes lora explicitly,
        // and a third field references {output_dir}. All three resolve in one Apply.
        var values = new Dictionary<string, object?>
        {
            ["lora"] = "foo",
            ["output_dir"] = "./results/{lora}/output"
        };
        Assert.Equal("./results/foo/output", ParameterTemplate.Apply("{output_dir}", values));
    }

    [Fact]
    public void Apply_caps_at_five_passes_to_break_cycles()
    {
        // a → {b}, b → {a}: would loop forever without a cap. The implementation
        // is allowed to leave residual placeholders; we just require it terminates
        // and returns a string with at least one placeholder still present.
        var values = new Dictionary<string, object?>
        {
            ["a"] = "{b}",
            ["b"] = "{a}"
        };
        var result = ParameterTemplate.Apply("{a}", values);
        Assert.Contains("{", result);
        Assert.True(result.Length < 100, "5-pass cap should keep the result bounded");
    }

    [Fact]
    public void Apply_formats_numbers_with_invariant_culture()
    {
        // Avoid culture surprises (1,5 vs 1.5) when the value is a double.
        var values = new Dictionary<string, object?> { ["x"] = 1.5 };
        Assert.Equal("x=1.5", ParameterTemplate.Apply("x={x}", values));
    }

    [Fact]
    public void Apply_renders_booleans_as_true_or_false()
    {
        var values = new Dictionary<string, object?>
        {
            ["yes"] = true,
            ["no"] = false
        };
        Assert.Equal("true/false", ParameterTemplate.Apply("{yes}/{no}", values));
    }

    [Fact]
    public void Apply_treats_null_value_as_unresolved()
    {
        // A null in the dictionary should leave the placeholder in place rather
        // than rendering "null".
        var values = new Dictionary<string, object?> { ["x"] = null };
        Assert.Equal("{x}", ParameterTemplate.Apply("{x}", values));
    }

    [Fact]
    public void ApplyAll_substitutes_each_template_in_the_list()
    {
        var values = new Dictionary<string, object?>
        {
            ["a"] = "1",
            ["b"] = "2"
        };
        var result = ParameterTemplate.ApplyAll(new[] { "x={a}", "y={b}" }, values);
        Assert.Equal(new[] { "x=1", "y=2" }, result);
    }
}
