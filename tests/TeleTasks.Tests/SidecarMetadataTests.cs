using TeleTasks.Services;
using Xunit;

namespace TeleTasks.Tests;

public sealed class SidecarMetadataTests : IDisposable
{
    private readonly string _root;

    public SidecarMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teletasks-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteImageWithSidecar(string baseName, string sidecarJson, string sidecarExt = ".json")
    {
        var image = Path.Combine(_root, baseName + ".png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var sidecar = Path.Combine(_root, baseName + sidecarExt);
        File.WriteAllText(sidecar, sidecarJson);
        return image;
    }

    [Fact]
    public void SiblingPath_finds_direct_extension_swap()
    {
        var img = WriteImageWithSidecar("frame_001", "{\"k\":1}");
        var sib = SidecarMetadata.SiblingPath(img, ".json");
        Assert.Equal(Path.Combine(_root, "frame_001.json"), sib);
    }

    [Fact]
    public void SiblingPath_strips_trailing_underscore_index_when_direct_misses()
    {
        // Render batch pattern: image is render-..._00.png but sidecar is render-....json
        // because the sidecar names the run, not the per-image index.
        var image = Path.Combine(_root, "render-20250101_1200_00.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var sidecar = Path.Combine(_root, "render-20250101_1200.json");
        File.WriteAllText(sidecar, "{\"prompt\":\"a forest\"}");

        var sib = SidecarMetadata.SiblingPath(image, ".json");
        Assert.Equal(sidecar, sib);
    }

    [Fact]
    public void SiblingPath_strips_trailing_dot_index()
    {
        var image = Path.Combine(_root, "render.005.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var sidecar = Path.Combine(_root, "render.json");
        File.WriteAllText(sidecar, "{\"k\":1}");

        var sib = SidecarMetadata.SiblingPath(image, ".json");
        Assert.Equal(sidecar, sib);
    }

    [Fact]
    public void SiblingPath_returns_direct_form_when_nothing_matches()
    {
        var image = Path.Combine(_root, "lonely.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var sib = SidecarMetadata.SiblingPath(image, ".json");
        // ReadFlatScalars will see the file as missing and return empty —
        // the contract says we hand back the direct form so callers know
        // where they expected to find it.
        Assert.Equal(Path.Combine(_root, "lonely.json"), sib);
    }

    [Fact]
    public void SiblingPath_normalises_extension_with_or_without_leading_dot()
    {
        var img = WriteImageWithSidecar("p", "{}");
        Assert.Equal(SidecarMetadata.SiblingPath(img, ".json"),
                     SidecarMetadata.SiblingPath(img, "json"));
    }

    [Fact]
    public void Read_reports_constant_keys_when_every_sidecar_agrees()
    {
        var a = WriteImageWithSidecar("a", "{\"model\":\"sdxl\",\"steps\":30,\"prompt\":\"forest\"}");
        var b = WriteImageWithSidecar("b", "{\"model\":\"sdxl\",\"steps\":30,\"prompt\":\"desert\"}");

        var batch = SidecarMetadata.Read(new[] { a, b }, ".json");

        // model + steps are constant; prompt varies between the two.
        Assert.Equal("sdxl", batch.Constant["model"]);
        Assert.Equal("30",   batch.Constant["steps"]);
        Assert.False(batch.Constant.ContainsKey("prompt"));

        Assert.Equal("forest", batch.Variable[0]["prompt"]);
        Assert.Equal("desert", batch.Variable[1]["prompt"]);
    }

    [Fact]
    public void Read_treats_a_field_missing_in_one_sidecar_as_variable()
    {
        // Even if the values that are present agree, a key missing in some
        // sidecars is variable — the diff is "you don't always have it".
        var a = WriteImageWithSidecar("a", "{\"prompt\":\"x\",\"lora\":\"foo\"}");
        var b = WriteImageWithSidecar("b", "{\"prompt\":\"x\"}");

        var batch = SidecarMetadata.Read(new[] { a, b }, ".json");

        Assert.Equal("x", batch.Constant["prompt"]);
        Assert.False(batch.Constant.ContainsKey("lora"));
        Assert.Equal("foo", batch.Variable[0]["lora"]);
        Assert.False(batch.Variable[1].ContainsKey("lora"));
    }

    [Fact]
    public void Read_skips_nested_objects_and_arrays()
    {
        // Only top-level scalars (string / number / bool) are considered. A
        // nested object would otherwise produce noisy stringified blobs.
        var a = WriteImageWithSidecar("a",
            "{\"prompt\":\"x\",\"clip\":{\"layer\":12},\"tags\":[\"a\",\"b\"]}");
        var batch = SidecarMetadata.Read(new[] { a }, ".json");

        Assert.True(batch.Constant.ContainsKey("prompt"));
        Assert.False(batch.Constant.ContainsKey("clip"));
        Assert.False(batch.Constant.ContainsKey("tags"));
    }

    [Fact]
    public void Read_handles_invalid_json_gracefully_as_empty_sidecar()
    {
        var image = Path.Combine(_root, "broken.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ this is: not valid json }");

        var batch = SidecarMetadata.Read(new[] { image }, ".json");

        // No exception, just no fields recovered.
        Assert.Empty(batch.Constant);
        Assert.Empty(batch.Variable[0]);
    }

    [Fact]
    public void Read_with_one_image_returns_all_fields_as_constant()
    {
        var a = WriteImageWithSidecar("only", "{\"prompt\":\"forest\",\"steps\":30}");
        var batch = SidecarMetadata.Read(new[] { a }, ".json");

        Assert.Equal(2, batch.Constant.Count);
        Assert.Equal("forest", batch.Constant["prompt"]);
        // Variable list still has one slot per image, but it's empty for the single-image case.
        Assert.Single(batch.Variable);
        Assert.Empty(batch.Variable[0]);
    }

    [Fact]
    public void BuildCaption_renders_default_format_when_no_template()
    {
        var fields = new Dictionary<string, string> { ["model"] = "sdxl", ["steps"] = "30" };
        var caption = SidecarMetadata.BuildCaption(fields, template: null, maxLength: 200);
        Assert.Contains("model: sdxl", caption);
        Assert.Contains("steps: 30", caption);
    }

    [Fact]
    public void BuildCaption_substitutes_placeholders_in_a_template()
    {
        var fields = new Dictionary<string, string> { ["prompt"] = "a forest", ["seed"] = "42" };
        var caption = SidecarMetadata.BuildCaption(fields, "prompt={prompt}, seed={seed}", maxLength: 200);
        Assert.Equal("prompt=a forest, seed=42", caption);
    }

    [Fact]
    public void BuildCaption_leaves_unresolved_placeholders_intact()
    {
        var fields = new Dictionary<string, string> { ["a"] = "1" };
        var caption = SidecarMetadata.BuildCaption(fields, "{a} / {missing}", maxLength: 200);
        Assert.Equal("1 / {missing}", caption);
    }

    [Fact]
    public void BuildCaption_truncates_to_maxLength()
    {
        var fields = new Dictionary<string, string> { ["k"] = new string('x', 500) };
        var caption = SidecarMetadata.BuildCaption(fields, template: null, maxLength: 50);
        Assert.True(caption.Length <= 50);
        Assert.EndsWith("...", caption);
    }
}
