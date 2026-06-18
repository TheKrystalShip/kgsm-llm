using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The hot-editable override layer: tool-description overlay from <c>tools.json</c> (names stay
/// structural, prose is replaced), with failure isolation — absent dir / absent file / bad JSON all
/// fall back to the in-code definitions rather than throwing or blanking.
/// </summary>
public sealed class FilePromptOverridesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-ovr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private FilePromptOverrides Make(bool withDir = true)
    {
        var settings = new Dictionary<string, string?>();
        if (withDir)
            settings[FilePromptOverrides.DirectoryKey] = _dir;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new FilePromptOverrides(config, NullLogger<FilePromptOverrides>.Instance);
    }

    private void WriteTools(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FilePromptOverrides.ToolsFileName), json);
    }

    private static IReadOnlyList<LlmToolDefinition> SampleTools() => new[]
    {
        LlmToolDefinition.Create(new Tool("get_status"), "BASE status desc",
            new LlmToolParameter("instance_name", "BASE param desc", Required: false)),
        LlmToolDefinition.Create(new Tool("list_blueprints"), "BASE list desc"),
    };

    [Fact]
    public void OverlayTools_ReplacesDescriptionAndParam_KeepsNameAndUnlistedTools()
    {
        WriteTools("""
        {
          "get_status": {
            "description": "TUNED status desc",
            "params": { "instance_name": "TUNED param desc" }
          }
        }
        """);

        var result = Make().OverlayTools(SampleTools());

        var status = result.Single(t => t.Name == "get_status");
        status.Description.Should().Be("TUNED status desc");
        status.Parameters.Single().Description.Should().Be("TUNED param desc");
        status.Parameters.Single().Name.Should().Be("instance_name");   // name is structural, untouched
        status.Parameters.Single().Required.Should().BeFalse();          // other facets preserved

        // A tool absent from tools.json is passed through unchanged.
        result.Single(t => t.Name == "list_blueprints").Description.Should().Be("BASE list desc");
    }

    [Fact]
    public void OverlayTools_PartialOverride_LeavesOmittedProseAtDefault()
    {
        WriteTools("""{ "get_status": { "description": "ONLY desc changed" } }""");

        var status = Make().OverlayTools(SampleTools()).Single(t => t.Name == "get_status");

        status.Description.Should().Be("ONLY desc changed");
        status.Parameters.Single().Description.Should().Be("BASE param desc");   // param untouched
    }

    [Fact]
    public void OverlayTools_NoDirectory_PassesThrough()
    {
        var tools = SampleTools();
        Make(withDir: false).OverlayTools(tools).Should().BeSameAs(tools);
    }

    [Fact]
    public void OverlayTools_AbsentFile_PassesThrough()
    {
        var tools = SampleTools();
        Make().OverlayTools(tools).Should().BeSameAs(tools);   // dir set, no tools.json
    }

    [Fact]
    public void OverlayTools_BadJson_PassesThrough_DoesNotThrow()
    {
        WriteTools("{ this is not json ");
        var tools = SampleTools();

        var act = () => Make().OverlayTools(tools);

        act.Should().NotThrow();
        act().Should().BeSameAs(tools);
    }

    [Fact]
    public void ReadText_ReturnsTrimmedContent_NullWhenBlankOrAbsent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "preamble.md"), "  hello  \n");
        File.WriteAllText(Path.Combine(_dir, "blank.md"), "   \n");

        var ovr = Make();
        ovr.ReadText("preamble.md").Should().Be("hello");
        ovr.ReadText("blank.md").Should().BeNull();      // blank ⇒ absent (mid-save safety)
        ovr.ReadText("missing.md").Should().BeNull();
    }
}
