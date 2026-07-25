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
    public async Task OwnershipIsNotInferred_FieldsStillExtract_DecidedEmpiricallyDownstream()
    {
        // A page saying "you must own the game" no longer stops synthesis — whether the SERVER files need
        // an owning account is measured by the anonymous test-install (an owned title downloads nothing),
        // not guessed here. So the fields extract normally and the pipeline proceeds to the install.
        const string url = "https://guide.example/starbound";
        ModelReturns($$"""
        { "self_hostable": true, "native_linux_server": true,
          "executable_file": "starbound_server", "executable_file_source": "{{url}}" }
        """);

        var findings = await Sut().SynthesizeAsync("Starbound",
            Page(url, "Run starbound_server to host. You must own Starbound on Steam to play."));

        findings!.Feasibility.Should().Be(BlueprintFeasibility.Feasible);
        Field(findings, "executable_file").Should().Be("starbound_server");
    }

    [Fact]
    public async Task FabricatedNonInstancePlaceholderInArgs_IsDropped_ButValidFieldsSurvive()
    {
        // The model invented $SERVER_PORT (KGSM defines no such variable → it resolves to empty at
        // runtime and the server never binds). The whole arg string is unreliable → dropped; the server
        // boots on its defaults. The genuinely-valid fields (executable, app id) are unaffected.
        const string url = "https://guide.example/valheim";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "valheim_server.x86_64", "executable_file_source": "{{url}}",
          "executable_arguments": "-port \"$SERVER_PORT\" -world $instance_level_name", "executable_arguments_source": "{{url}}",
          "steam_app_id": "896660", "steam_app_id_source": "{{url}}"
        }
        """);

        var findings = await Sut().SynthesizeAsync("Valheim",
            Page(url, "Run valheim_server.x86_64 -port <PORT> -world [World]. steamcmd +app_update 896660."));

        findings!.Feasibility.Should().Be(BlueprintFeasibility.Feasible);
        findings.Fields.Should().NotContain(f => f.Name == "executable_arguments",
            "a fabricated $SERVER_PORT makes the whole arg string resolve-to-empty at runtime");
        Field(findings, "executable_file").Should().Be("valheim_server.x86_64");
        Field(findings, "steam_app_id").Should().Be("896660");
    }

    [Fact]
    public async Task InstancePlaceholdersOnly_InArgs_PassTheForeignPlaceholderGuard()
    {
        const string url = "https://guide.example/necesse";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "StartServer-nogui.sh", "executable_file_source": "{{url}}",
          "executable_arguments": "-world $instance_level_name -localdir $instance_saves_dir", "executable_arguments_source": "{{url}}"
        }
        """);

        var findings = await Sut().SynthesizeAsync("Necesse",
            Page(url, "Run StartServer-nogui.sh -world [name] -localdir [dir]."));

        Field(findings!, "executable_arguments").Should().Be("-world $instance_level_name -localdir $instance_saves_dir");
    }

    [Fact]
    public async Task JavaInterpreterShape_ReportsInterpreterAsExecutable_JarInArgs()
    {
        const string url = "https://guide.example/mc";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "java", "executable_file_source": "{{url}}",
          "executable_arguments": "-Xmx4G -jar server.jar nogui", "executable_arguments_source": "{{url}}"
        }
        """);

        var findings = await Sut().SynthesizeAsync("Minecraft",
            Page(url, "Start the server with: java -Xmx4G -jar server.jar nogui"));

        Field(findings!, "executable_file").Should().Be("java", "an interpreter-launched server runs THROUGH java, which is the executable");
        Field(findings!, "executable_arguments").Should().Be("-Xmx4G -jar server.jar nogui");
    }

    [Fact]
    public async Task ExecutableSubdirectory_AndClientAppId_AreExtracted()
    {
        const string url = "https://guide.example/factorio";
        ModelReturns($$"""
        {
          "self_hostable": true, "native_linux_server": true,
          "executable_file": "factorio", "executable_file_source": "{{url}}",
          "executable_subdirectory": "bin/x64", "executable_subdirectory_source": "{{url}}",
          "steam_app_id": null,
          "client_steam_app_id": "427520", "client_steam_app_id_source": "{{url}}"
        }
        """);

        var findings = await Sut().SynthesizeAsync("Factorio",
            Page(url, "Run bin/x64/factorio to start the server. Its Steam app id is 427520."));

        Field(findings!, "executable_file").Should().Be("factorio", "the subdirectory is split out, not crammed into the filename");
        Field(findings!, "executable_subdirectory").Should().Be("bin/x64");
        Field(findings!, "client_steam_app_id").Should().Be("427520");
    }

    private static string? Field(BlueprintResearchFindings findings, string name) =>
        findings.Fields.FirstOrDefault(f => f.Name == name)?.Value;
}
