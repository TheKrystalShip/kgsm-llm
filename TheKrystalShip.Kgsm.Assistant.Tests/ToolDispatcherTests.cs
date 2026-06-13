using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the dispatcher's routing, name resolution, and confirmation staging
/// against the host ports (<see cref="IServerOperations"/> / <see cref="IServerInventory"/>),
/// which the host implements over whatever it uses to talk to kgsm.
/// </summary>
public class ToolDispatcherTests
{
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly ConfirmationContext _confirmations = new();

    public ToolDispatcherTests()
    {
        // Two terraria-* instances (matched by substring / game type) plus a unique minecraft.
        var instances = new Dictionary<string, string>
        {
            ["terraria-pvp"] = "terraria",
            ["terraria-creative"] = "terraria",
            ["minecraft"] = "minecraft",
        };
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)instances);

        var blueprints = new[] { "valheim", "terraria" };
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)blueprints);
    }

    private ToolDispatcher Create() =>
        new(_operations, _inventory, _confirmations, NullLogger<ToolDispatcher>.Instance);

    private static LlmToolCall Call(string name, string instance) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = instance });

    private static LlmToolCall InstallCall(string blueprint, string? name = null) =>
        new(LlmTools.InstallServer, new Dictionary<string, string?>
        {
            ["blueprint_name"] = blueprint,
            ["instance_name"] = name,
        });

    [Fact]
    public async Task ExactName_Resolves_AndExecutes()
    {
        _operations.GetStatusAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success("running, pid 123"));

        var result = await Create().ExecuteAsync(Call(LlmTools.GetStatus, "minecraft"));

        result.Should().Contain("Status for minecraft");
        await _operations.Received(1).GetStatusAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleFuzzyMatch_Resolves()
    {
        _operations.GetStatusAsync("terraria-pvp", Arg.Any<CancellationToken>())
            .Returns(Result.Success("stopped"));

        // "pvp" is a substring of exactly one instance.
        await Create().ExecuteAsync(Call(LlmTools.GetStatus, "pvp"));

        await _operations.Received(1).GetStatusAsync("terraria-pvp", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousName_AsksUser_AndDoesNotExecute()
    {
        // "terraria" matches two instances by game type / substring.
        var result = await Create().ExecuteAsync(Call(LlmTools.GetStatus, "terraria"));

        result.Should().Contain("Ambiguous")
            .And.Contain("terraria-pvp")
            .And.Contain("terraria-creative");
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownName_ReturnsMiss_WithKnownList()
    {
        var result = await Create().ExecuteAsync(Call(LlmTools.GetStatus, "doesnotexist"));

        result.Should().Contain("no instance named").And.Contain("minecraft");
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_NoInstanceName_ReturnsFleetSummary_InASingleBulkCall()
    {
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FleetStatusEntry>>(new[]
            {
                new FleetStatusEntry("minecraft", FleetStatusAvailability.Read, true, null),
                new FleetStatusEntry("terraria-pvp", FleetStatusAvailability.Read, false, null),
            }));

        var result = await Create().ExecuteAsync(
            new LlmToolCall(LlmTools.GetStatus, new Dictionary<string, string?>()));

        result.Should().Contain("minecraft: running").And.Contain("terraria-pvp: stopped");

        // The MaxIterations fix: one bulk call, never a per-instance fan-out.
        await _operations.Received(1).GetFleetStatusAsync(Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_Fleet_UnreadableInstance_IsUnavailable_NeverStopped()
    {
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FleetStatusEntry>>(new[]
            {
                new FleetStatusEntry("broken", FleetStatusAvailability.Unavailable, null,
                    "its management file must be regenerated to report status"),
            }));

        var result = await Create().ExecuteAsync(
            new LlmToolCall(LlmTools.GetStatus, new Dictionary<string, string?>()));

        // The §3.7 guard: a could-not-read instance must not masquerade as stopped.
        result.Should().Contain("status unavailable").And.Contain("regenerated");
        result.Should().NotContain("stopped");
    }

    [Fact]
    public async Task ViewConfigFile_ReadsResolvedInstanceConfig_AndRedactsSecrets()
    {
        _operations.ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>())
            .Returns(Result.Success("port = 25565\nrcon_password = hunter2\nlevel = world"));

        var result = await Create().ExecuteAsync(Call(LlmTools.ViewConfigFile, "minecraft"));

        // The filename is derived from the resolved instance name (no model-supplied path).
        await _operations.Received(1)
            .ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>());

        result.Should().Contain("port = 25565").And.Contain("level = world");
        result.Should().Contain("rcon_password").And.Contain("***redacted***");
        result.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task ViewConfigFile_UnknownInstance_DoesNotRead()
    {
        var result = await Create().ExecuteAsync(Call(LlmTools.ViewConfigFile, "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .ReadInstanceFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Create().ExecuteAsync(
            new LlmToolCall("delete_everything", new Dictionary<string, string?>()));

        result.Should().Contain("not a known tool");
    }

    public static IEnumerable<object[]> StagedCommandCases() => new[]
    {
        new object[] { LlmTools.StartServer, ConfirmationKind.Start },
        new object[] { LlmTools.StopServer, ConfirmationKind.Stop },
        new object[] { LlmTools.RestartServer, ConfirmationKind.Restart },
        new object[] { LlmTools.CreateBackup, ConfirmationKind.Backup },
        new object[] { LlmTools.UpdateServer, ConfirmationKind.Update },
    };

    [Theory]
    [MemberData(nameof(StagedCommandCases))]
    public async Task Command_StagesConfirmation_AndDoesNotExecuteInline(string tool, ConfirmationKind kind)
    {
        // §3.5: every command is propose-only. The dispatcher resolves + stages it; the
        // single-instance op runs later (from ConfirmAsync), never here in the agent loop.
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(tool, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(kind, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");

        // None of the mutating ops fired inline — staging only.
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateBackupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_UnresolvedTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.StartServer, "doesnotexist"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallServer_StagesConfirmation_AndDoesNotExecute()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
    }

    [Fact]
    public async Task UninstallServer_AmbiguousTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "terraria"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task InstallServer_ResolvesBlueprint_AndStagesConfirmation()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "my-valheim"));

            result.Should().Contain("Staged");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new PendingConfirmation(ConfirmationKind.Install, "valheim", "my-valheim"));
        }
    }

    [Fact]
    public async Task InstallServer_NameCollision_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "minecraft"));

            result.Should().Contain("already exists");
            _confirmations.Staged.Should().BeEmpty();
        }
    }
}
