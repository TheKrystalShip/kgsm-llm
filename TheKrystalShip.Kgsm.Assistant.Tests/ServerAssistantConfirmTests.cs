using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies <see cref="ServerAssistant.ConfirmAsync"/> — the model-independent execution
/// gate for staged commands (§3.5: every command is propose-only): authority is re-checked,
/// the target is re-validated against live inventory (which also blunts token replay), and
/// only then does it execute.
/// </summary>
public class ServerAssistantConfirmTests
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly INetworkInfo _network = Substitute.For<INetworkInfo>();
    private readonly IUpnpInfo _upnp = Substitute.For<IUpnpInfo>();

    private ServerAssistant Create() => new(
        Substitute.For<ILlmAgent>(),
        Substitute.For<ISystemPromptBuilder>(),
        new ConfirmationContext(),
        _inventory,
        _operations,
        _network,
        _upnp,
        new NoopToolRelevanceFilter(),
        new PassthroughPromptOverrides(),
        Options.Create(new SearchOptions()),
        NullLogger<ServerAssistant>.Instance);

    private void Instances(params string[] names) =>
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                names.ToDictionary(n => n, _ => "game")));

    private void Blueprints(params string[] names) =>
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(names));

    [Fact]
    public async Task UnauthorizedCaller_IsRefused_AndNothingExecutes()
    {
        Instances("terraria");
        var confirmation = new PendingConfirmation(ConfirmationKind.Uninstall, "terraria");

        var result = await Create().ConfirmAsync(confirmation, canPerformActions: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("permission");
        await _operations.DidNotReceive().UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uninstall_HappyPath_ExecutesAndReportsOutcome()
    {
        Instances("terraria");
        _operations.UninstallAsync("terraria", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("Uninstalled").And.Contain("terraria");
        await _operations.Received(1).UninstallAsync("terraria", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uninstall_TargetGone_IsRefused_WithoutExecuting()
    {
        Instances(); // the instance vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer exists");
        await _operations.DidNotReceive().UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_NameCollision_IsRefused_WithoutExecuting()
    {
        Blueprints("valheim");
        Instances("valheim"); // requested name now collides
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Install, "valheim", "valheim"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
        await _operations.DidNotReceive().InstallAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_HappyPath_ExecutesWithResolvedBlueprint()
    {
        Blueprints("valheim");
        Instances(); // no collision
        _operations.InstallAsync("valheim", "myserver", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Install, "valheim", "myserver"), canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("Installed").And.Contain("valheim");
        await _operations.Received(1).InstallAsync("valheim", "myserver", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_BlueprintGone_IsRefused_WithoutExecuting()
    {
        Blueprints(); // blueprint no longer available
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Install, "valheim"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer available");
        await _operations.DidNotReceive().InstallAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uninstall_OperationFails_SurfacesError()
    {
        Instances("terraria");
        _operations.UninstallAsync("terraria", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("kgsm exploded")));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("kgsm exploded");
    }

    // --- §3.8 set-config (key=value) -------------------------------------------------------

    [Fact]
    public async Task SetConfig_HappyPath_ExecutesAndReportsOutcome()
    {
        Instances("minecraft");
        _operations.SetInstanceConfigValueAsync("minecraft", "auto_update", "true", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "auto_update", ConfigValue: "true"),
            canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("minecraft").And.Contain("auto_update").And.Contain("true");
        await _operations.Received(1)
            .SetInstanceConfigValueAsync("minecraft", "auto_update", "true", Arg.Any<CancellationToken>());
    }

    // --- open_ports (host-firewall) --------------------------------------------------------

    [Fact]
    public async Task OpenPorts_HappyPath_OpensViaFirewall_AndReportsOutcome()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.Applied, "ufw", null)));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: null, ConfigValue: "34197/udp"),
            canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("factorio").And.Contain("34197/udp");
        await _network.Received(1).OpenPortsAsync(
            "factorio",
            Arg.Is<IReadOnlyList<PortRule>>(p => p.Count == 1 && p[0] == new PortRule(34197, 34197, "udp")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPorts_FirewallUnreachable_FailsHonestly_NoFabricatedOpen()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.FirewallUnavailable, "", null)));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: null, ConfigValue: "34197/udp"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("isn't reachable").And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task OpenPorts_TargetGone_IsRefused_WithoutOpening()
    {
        Instances(); // vanished since staging
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: null, ConfigValue: "34197/udp"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer exists");
        await _network.DidNotReceive().OpenPortsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPorts_UnauthorizedCaller_IsRefused_AndNothingOpens()
    {
        Instances("factorio");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: null, ConfigValue: "34197/udp"),
            canPerformActions: false);

        result.IsFailure.Should().BeTrue();
        await _network.DidNotReceive().OpenPortsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPorts_DefaultScope_DoesNotTouchTheRouter()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.Applied, "ufw", null)));

        await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: null, ConfigValue: "34197/udp"),  // no router scope
            canPerformActions: true);

        await _upnp.DidNotReceive().OpenForwardsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>());
    }

    // --- open_ports with the router leg opted in (ConfigKey == "router") -------------------

    [Fact]
    public async Task OpenPorts_Router_OpensBoth_AndReportsBothOutcomes()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.Applied, "ufw", null)));
        _upnp.OpenForwardsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UpnpActionResult(UpnpActionState.Applied, null)));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: "router", ConfigValue: "34197/udp"),
            canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("host-firewall").And.Contain("router/UPnP forward");
        await _upnp.Received(1).OpenForwardsAsync(
            "factorio",
            Arg.Is<IReadOnlyList<PortRule>>(p => p.Count == 1 && p[0] == new PortRule(34197, 34197, "udp")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPorts_Router_GatedOff_ReportsSkipped_NotFabricated()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.Applied, "ufw", null)));
        _upnp.OpenForwardsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UpnpActionResult(UpnpActionState.Skipped, null)));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: "router", ConfigValue: "34197/udp"),
            canPerformActions: true);

        // Firewall still succeeded; the router leg is honestly "skipped", never a fabricated forward.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("skipped").And.Contain("port-forwarding disabled");
    }

    [Fact]
    public async Task OpenPorts_Router_WatchdogUnreachable_FirewallStandsPlusHonestRouterClause()
    {
        Instances("factorio");
        _network.OpenPortsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NetworkActionResult(NetworkActionState.Applied, "ufw", null)));
        _upnp.OpenForwardsAsync("factorio", Arg.Any<IReadOnlyList<PortRule>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UpnpActionResult(UpnpActionState.Unavailable, null)));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.OpenPorts, "factorio",
                InstanceName: null, ConfigKey: "router", ConfigValue: "34197/udp"),
            canPerformActions: true);

        result.IsSuccess.Should().BeTrue();  // firewall opened; the router clause is additive context
        result.Value.Should().Contain("watchdog isn't reachable");
    }

    // --- write_file --------------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_HappyPath_WritesAndReportsOutcome()
    {
        Instances("minecraft");
        _operations.WriteInstanceFileAsync("minecraft", "server.properties", "motd=hi", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: "server.properties", ConfigValue: "motd=hi"),
            canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("server.properties").And.Contain("minecraft");
        await _operations.Received(1).WriteInstanceFileAsync(
            "minecraft", "server.properties", "motd=hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_TargetGone_IsRefused_WithoutWriting()
    {
        Instances(); // vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: "server.properties", ConfigValue: "motd=hi"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer exists");
        await _operations.DidNotReceive().WriteInstanceFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_UnauthorizedCaller_IsRefused_AndNothingWrites()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: "server.properties", ConfigValue: "motd=hi"),
            canPerformActions: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("permission");
        await _operations.DidNotReceive().WriteInstanceFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_OperationFails_SurfacesError()
    {
        Instances("minecraft");
        _operations.WriteInstanceFileAsync("minecraft", "server.properties", "motd=hi", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("jail refused it")));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: "server.properties", ConfigValue: "motd=hi"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("jail refused it");
    }

    [Fact]
    public async Task WriteFile_MissingPath_IsRefused_WithoutWriting()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: null, ConfigValue: "motd=hi"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        await _operations.DidNotReceive().WriteInstanceFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetConfig_TargetGone_IsRefused_WithoutExecuting()
    {
        Instances(); // vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "auto_update", ConfigValue: "true"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer exists");
        await _operations.DidNotReceive().SetInstanceConfigValueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetConfig_UnauthorizedCaller_IsRefused_AndNothingExecutes()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "auto_update", ConfigValue: "true"),
            canPerformActions: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("permission");
        await _operations.DidNotReceive().SetInstanceConfigValueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetConfig_KgsmRefusesProtectedKey_SurfacesError()
    {
        // kgsm owns the denylist; a refused (protected) key comes back as a failed Result.
        Instances("minecraft");
        _operations.SetInstanceConfigValueAsync("minecraft", "name", "evil", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("'name' is a protected key and cannot be set with config-set")));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.SetConfig, "minecraft",
                InstanceName: null, ConfigKey: "name", ConfigValue: "evil"),
            canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("protected key");
    }

    // --- §3.5 generalised commands (start/stop/restart/update/backup) -----------------------

    public static IEnumerable<object[]> CommandKinds() => new[]
    {
        new object[] { ConfirmationKind.Start, "started" },
        new object[] { ConfirmationKind.Stop, "stopped" },
        new object[] { ConfirmationKind.Restart, "restarted" },
        new object[] { ConfirmationKind.Update, "updated" },
        new object[] { ConfirmationKind.Backup, "backed up" },
    };

    [Theory]
    [MemberData(nameof(CommandKinds))]
    public async Task Command_HappyPath_ExecutesAndReportsOutcome(ConfirmationKind kind, string pastTense)
    {
        Instances("minecraft");
        StubOp(kind, "minecraft", Result.Success());

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(kind, "minecraft"), canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("minecraft").And.Contain(pastTense);
        await ReceivedOp(kind, "minecraft");
    }

    [Fact]
    public async Task Command_TargetGone_IsRefused_WithoutExecuting()
    {
        Instances(); // the instance vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Start, "minecraft"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer exists");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_UnauthorizedCaller_IsRefused_AndNothingExecutes()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Start, "minecraft"), canPerformActions: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("permission");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_OperationFails_SurfacesError()
    {
        Instances("minecraft");
        _operations.StartAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("kgsm exploded")));

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Start, "minecraft"), canPerformActions: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("kgsm exploded");
    }

    private void StubOp(ConfirmationKind kind, string instance, Result r)
    {
        var task = Task.FromResult(r);
        switch (kind)
        {
            case ConfirmationKind.Start: _operations.StartAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Stop: _operations.StopAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Restart: _operations.RestartAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Update: _operations.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Backup: _operations.CreateBackupAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private async Task ReceivedOp(ConfirmationKind kind, string instance)
    {
        switch (kind)
        {
            case ConfirmationKind.Start: await _operations.Received(1).StartAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Stop: await _operations.Received(1).StopAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Restart: await _operations.Received(1).RestartAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Update: await _operations.Received(1).UpdateAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Backup: await _operations.Received(1).CreateBackupAsync(instance, Arg.Any<CancellationToken>()); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
