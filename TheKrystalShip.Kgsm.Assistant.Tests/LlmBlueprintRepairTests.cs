using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Evidence-driven repair with its anti-fabrication guards. Because the evidence is ground truth (the real
/// install tree, the boot log), the checks are stronger than synthesis's: a proposed executable that is
/// NOT in the install tree is a hallucination and dropped; a foreign <c>$VARIABLE</c> in the args is
/// dropped (it resolves to empty and breaks the boot); and a proposal that changes nothing returns null so
/// the pipeline stops instead of re-running an identical draft.
/// </summary>
public sealed class LlmBlueprintRepairTests
{
    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();

    private LlmBlueprintRepair Sut() => new(_llm, NullLogger<LlmBlueprintRepair>.Instance);

    private void ModelReturns(string json) =>
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(LlmResponse.Text(json)));

    private static BlueprintRepairContext Context(
        string installTree = "start_server_bepinex.sh (file, 512 bytes)\nvalheim_server.x86_64 (file, 9000000 bytes)",
        string launchScripts = "=== install/start_server_bepinex.sh ===\nexport LD_LIBRARY_PATH=./linux64\n./valheim_server.x86_64 -nographics -serverport 2456",
        string bootLog = "unknown argument -port") =>
        new("Valheim", "valheim_server.x86_64", "", "-port 2456", "", "2456/tcp|2456/udp",
            InstallSucceeded: true, InstallError: null, installTree, launchScripts, bootLog, PortsReachable: false);

    [Fact]
    public async Task ProposedExecutableInTree_IsAccepted()
    {
        ModelReturns("""
        { "executable_file": "start_server_bepinex.sh", "executable_subdirectory": null,
          "executable_arguments": "", "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context());

        proposal.Should().NotBeNull();
        proposal!.ExecutableFile.Should().Be("start_server_bepinex.sh", "the wrapper script is right there in the install tree");
        proposal.ExecutableArguments.Should().Be("", "an empty string is a meaningful 'clear the args' proposal");
    }

    [Fact]
    public async Task ProposedExecutableNotInTree_IsDropped()
    {
        // The model invents a filename the install never produced — ground truth says it isn't on disk.
        ModelReturns("""
        { "executable_file": "imaginary_server.sh", "executable_subdirectory": null,
          "executable_arguments": null, "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context());

        // Only field it proposed was the fabricated executable → nothing left to change → null (stop).
        proposal.Should().BeNull();
    }

    [Fact]
    public async Task ForeignPlaceholderInArgs_IsDropped_ButValidFieldsSurvive()
    {
        ModelReturns("""
        { "executable_file": "start_server_bepinex.sh",
          "executable_arguments": "-serverport $SERVER_PORT", "executable_subdirectory": null,
          "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context());

        proposal.Should().NotBeNull();
        proposal!.ExecutableFile.Should().Be("start_server_bepinex.sh");
        proposal.ExecutableArguments.Should().BeNull("$SERVER_PORT is not a real KGSM placeholder — it resolves to empty and breaks the boot, so the args proposal is dropped");
    }

    [Fact]
    public async Task InstancePlaceholderInArgs_PassesTheGuard()
    {
        ModelReturns("""
        { "executable_file": "start_server_bepinex.sh",
          "executable_arguments": "-world $instance_level_name", "executable_subdirectory": null,
          "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context());

        proposal!.ExecutableArguments.Should().Be("-world $instance_level_name", "the real $instance_* placeholders are allowed");
    }

    [Fact]
    public async Task AllNullProposal_ReturnsNull_SoThePipelineStops()
    {
        ModelReturns("""
        { "executable_file": null, "executable_subdirectory": null, "executable_arguments": null,
          "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context());

        proposal.Should().BeNull();
    }

    [Fact]
    public async Task ClearingTheSubdirectory_IsDistinctFromLeavingItUnchanged()
    {
        // Empty string means "the binary is at the install root — clear the subdir"; that is a real change,
        // not a no-op, so it must survive as an empty (non-null) proposal.
        ModelReturns("""
        { "executable_file": null, "executable_subdirectory": "", "executable_arguments": null,
          "startup_success_regex": null, "ports": null }
        """);

        var proposal = await Sut().RepairAsync(Context() with { ExecutableSubdirectory = "bin/x64" });

        proposal.Should().NotBeNull();
        proposal!.ExecutableSubdirectory.Should().Be("");
    }

    [Fact]
    public async Task UnparseableModelReply_ReturnsNull()
    {
        ModelReturns("I couldn't figure this one out, sorry.");

        var proposal = await Sut().RepairAsync(Context());

        proposal.Should().BeNull();
    }

    [Fact]
    public async Task PortNumberProposal_IsNormalizedToDigits()
    {
        ModelReturns("""
        { "executable_file": null, "executable_subdirectory": null, "executable_arguments": null,
          "startup_success_regex": null, "ports": "port 2457/udp" }
        """);

        var proposal = await Sut().RepairAsync(Context());

        proposal!.Ports.Should().Be("2457", "the caller re-renders a bare port number to UFW form");
    }
}
