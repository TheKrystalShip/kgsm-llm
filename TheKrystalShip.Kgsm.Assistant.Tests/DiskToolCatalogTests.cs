using FluentAssertions;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The catalog is the contract between the model and the dispatcher. These pin the ways that contract
/// can be broken by an edit, because every one of them is silent at the model's end: a tool that
/// vanishes just stops being used, and one that is invented fails only on the turn it is called.
/// </summary>
public sealed class DiskToolCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-tools-" + Guid.NewGuid().ToString("N"));

    public DiskToolCatalogTests() => ShippedText.SeedInto(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string ToolsPath => Path.Combine(_dir, DiskToolCatalog.FileName);

    private void Rewrite(Func<string, string> edit) => File.WriteAllText(ToolsPath, edit(File.ReadAllText(ToolsPath)));

    private Action Load => () => _ = new DiskToolCatalog(_dir);

    [Fact]
    public void ShippedFile_DescribesExactlyTheDispatchableTools()
    {
        var catalog = new DiskToolCatalog(_dir);

        // The ordinary-turn offer excludes revise_blueprint; the catalog still defines it.
        catalog.All.Select(t => t.Tool).Should().BeEquivalentTo(LlmTools.AllToolNames);
        catalog.ReadOnly.Select(t => t.Tool).Should().BeEquivalentTo(LlmTools.ReadOnlyTier);
        catalog.ReviseBlueprintTool.Tool.Should().Be(LlmTools.ReviseBlueprint);
    }

    [Fact]
    public void EnumsAndRequiredFlags_SurviveTheRoundTrip()
    {
        var verb = new DiskToolCatalog(_dir).All
            .Single(t => t.Tool == LlmTools.ServerCommand)
            .Parameters.Single(p => p.Name == "verb");

        verb.Required.Should().BeTrue();
        verb.AllowedValues.Should().Contain(["start", "stop", "restart"]);
    }

    [Fact]
    public void MissingFile_IsRefused()
    {
        File.Delete(ToolsPath);

        Load.Should().Throw<AssistantTextUnavailableException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void DroppedTool_IsRefused_RatherThanSilentlyRemovingACapability()
    {
        Rewrite(json => json.Replace("\"host_info\"", "\"host_info_disabled\""));

        Load.Should().Throw<AssistantTextUnavailableException>().WithMessage("*host_info*");
    }

    [Fact]
    public void InventedTool_IsRefused_BecauseNothingCouldRunIt()
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ToolsPath))!.AsObject();
        doc["delete_everything"] = new System.Text.Json.Nodes.JsonObject
        {
            ["description"] = "Delete everything.",
            ["params"] = new System.Text.Json.Nodes.JsonArray(),
        };
        File.WriteAllText(ToolsPath, doc.ToJsonString());

        Load.Should().Throw<AssistantTextUnavailableException>()
            .WithMessage("*no handler*delete_everything*");
    }

    [Fact]
    public void BlankToolDescription_IsRefused_BecauseItSilentlyRemovesTheToolFromPlay()
    {
        // Edited as JSON rather than by regex: a description legitimately contains escaped quotes,
        // and a textual substitution walks straight through one and produces invalid JSON instead of
        // the blank description this is about.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ToolsPath))!.AsObject();
        doc["host_info"]!["description"] = "";
        File.WriteAllText(ToolsPath, doc.ToJsonString());

        Load.Should().Throw<AssistantTextUnavailableException>().WithMessage("*no description*");
    }

    [Fact]
    public void UnknownParameterType_IsRefused()
    {
        Rewrite(json => json.Replace("\"type\": \"string\"", "\"type\": \"str\"", StringComparison.Ordinal));

        Load.Should().Throw<AssistantTextUnavailableException>().WithMessage("*not one of*");
    }

    [Fact]
    public void MalformedJson_IsRefused_WithTheParseError()
    {
        File.WriteAllText(ToolsPath, "{ this is not json ");

        Load.Should().Throw<AssistantTextUnavailableException>().WithMessage("*not valid JSON*");
    }
}
