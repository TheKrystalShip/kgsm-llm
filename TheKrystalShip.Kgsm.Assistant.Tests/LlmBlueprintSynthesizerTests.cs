using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Model-synthesis extraction with its anti-fabrication guards. The load-bearing behaviours: a KGSM
/// <c>$instance_*</c> placeholder the model substitutes into the launch args (mapping a doc's
/// "[Save name]" onto <c>$instance_level_name</c>) survives extraction intact — it is a controlled
/// substitution, not a verbatim copy, so the copy-from-source check must not strip it; and a field whose
/// value the fetched pages don't actually contain (a copy-from-source fact the model invented) is dropped.
/// </summary>
public sealed class LlmBlueprintSynthesizerTests
{
    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();

    private LlmBlueprintSynthesizer Sut() => new(_llm, NullLogger<LlmBlueprintSynthesizer>.Instance);

    private void ModelReturns(string json) =>
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(LlmResponse.Text(json)));

    private static IReadOnlyList<(string, string)> Page(string url, string text) => [(url, text)];

    [Fact]
    public async Task KgsmInstancePlaceholderInArgs_SurvivesExtraction()
    {
        const string url = "https://necessewiki.com/Multiplayer-Linux";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "StartServer-nogui.sh", "executable_file_source": "{{url}}",
          "executable_arguments": "-world $instance_level_name", "executable_arguments_source": "{{url}}",
          "ports": "14159", "ports_source": "{{url}}",
          "steam_app_id": "1169370", "steam_app_id_source": "{{url}}",
          "startup_success_regex": null, "startup_success_regex_source": null
        }
        """);

        var findings = await Sut().SynthesizeAsync("Necesse",
            Page(url, "Run StartServer-nogui.sh with -world [Save name]. steamcmd +app_update 1169370. Port 14159."));

        findings!.Feasibility.Should().Be(BlueprintFeasibility.Feasible);
        Field(findings, "executable_arguments").Should().Be("-world $instance_level_name", "a KGSM $instance_* placeholder is a controlled substitution, not stripped as 'not in the page'");
        Field(findings, "executable_file").Should().Be("StartServer-nogui.sh");
        Field(findings, "steam_app_id").Should().Be("1169370");
    }

    [Fact]
    public async Task InventedCopyFromSourceField_NotInAnyPage_IsDropped()
    {
        const string url = "https://guide.example/x";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "made-up-server.sh", "executable_file_source": "{{url}}"
        }
        """);

        // The page never mentions "made-up-server.sh"; the verbatim-in-text guard drops it, so with no
        // required field synthesis is inconclusive (null → the aggregator falls back to the extractor).
        var findings = await Sut().SynthesizeAsync("X",
            Page(url, "This game has a Linux dedicated server but the page doesn't name the launch script."));

        findings.Should().BeNull();
    }

    [Fact]
    public async Task RequiresOwnedSteamAccount_SurfacesAsAFeasibilityStop_BeforeAnyDraft()
    {
        ModelReturns("""
        { "self_hostable": true, "native_linux_server": true, "requires_steam_account": true }
        """);

        var findings = await Sut().SynthesizeAsync("Starbound",
            Page("https://guide.example/starbound", "You must own Starbound on Steam; steamcmd +login <username> is required."));

        findings!.Feasibility.Should().Be(BlueprintFeasibility.RequiresSteamAccount);
        findings.Fields.Should().BeEmpty("an account-gated game stops before field extraction — nothing to draft");
    }

    private static string? Field(BlueprintResearchFindings findings, string name) =>
        findings.Fields.FirstOrDefault(f => f.Name == name)?.Value;
}
