using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies <see cref="ServerAssistant.ConfirmAsync"/> — the model-independent execution
/// gate for staged commands (every command is propose-only): authority is re-checked,
/// the target is re-validated against live inventory (which also blunts token replay), and
/// only then does it execute.
/// </summary>
public class ServerAssistantConfirmTests
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();

    private ServerAssistant Create() => new(
        Substitute.For<ILlmAgent>(),
        Substitute.For<ISystemPromptBuilder>(),
        new ConfirmationContext(),
        Substitute.For<ITurnProgress>(),
        _inventory,
        _operations,
        new NoopToolRelevanceFilter(),
        new PassthroughPromptOverrides(),
        Substitute.For<IBlueprintAuthoring>(),
        Options.Create(new SearchOptions()),
        Options.Create(new FetchOptions()),
        Options.Create(new BlueprintAuthoringFlags()),
        SettlementTiming.Default,
        NullLogger<ServerAssistant>.Instance);

    private void Instances(params string[] names) =>
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                names.ToDictionary(n => n, _ => "game")));

    private void Blueprints(params string[] names) =>
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(names));

    /// <summary>
    /// Stubs the run-state read the settle step makes after a lifecycle command. There is no default:
    /// a lifecycle test that does not say what the server did afterwards has not described its own
    /// scenario, and the confirm path is entitled to fail loudly rather than assume.
    /// </summary>
    private void Fleet(string instance, bool running) =>
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<IReadOnlyList<FleetStatusEntry>>(
                [new FleetStatusEntry(instance, FleetStatusAvailability.Read, running, null)])));

    /// <summary>Stubs a run-state read that could not be taken — the honest "we could not look".</summary>
    private void FleetUnreadable(string instance, string reason) =>
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<IReadOnlyList<FleetStatusEntry>>(
                [new FleetStatusEntry(instance, FleetStatusAvailability.Unavailable, null, reason)])));

    [Fact]
    public async Task UnauthorizedCaller_IsRefused_AndNothingExecutes()
    {
        Instances("terraria");
        var confirmation = new PendingConfirmation(ConfirmationKind.Uninstall, "terraria");

        var result = await Create().ConfirmAsync(confirmation, canPerformActions: false);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("permission");
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

        result.Ok.Should().BeTrue();
        result.Summary.Should().Contain("Uninstalled").And.Contain("terraria");
        await _operations.Received(1).UninstallAsync("terraria", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uninstall_TargetGone_IsRefused_WithoutExecuting()
    {
        Instances(); // the instance vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), canPerformActions: true);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("no longer exists");
        await _operations.DidNotReceive().UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_NameCollision_IsRefused_WithoutExecuting()
    {
        Blueprints("valheim");
        Instances("valheim"); // requested name now collides
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Install, "valheim", "valheim"), canPerformActions: true);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("already exists");
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

        result.Ok.Should().BeTrue();
        result.Summary.Should().Contain("Installed").And.Contain("valheim");
        await _operations.Received(1).InstallAsync("valheim", "myserver", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_BlueprintGone_IsRefused_WithoutExecuting()
    {
        Blueprints(); // blueprint no longer available
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Install, "valheim"), canPerformActions: true);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("no longer available");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("kgsm exploded");
    }

    // --- set-config (key=value) ------------------------------------------------------------

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

        result.Ok.Should().BeTrue();
        result.Summary.Should().Contain("minecraft").And.Contain("auto_update").And.Contain("true");
        await _operations.Received(1)
            .SetInstanceConfigValueAsync("minecraft", "auto_update", "true", Arg.Any<CancellationToken>());
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

        result.Ok.Should().BeTrue();
        result.Summary.Should().Contain("server.properties").And.Contain("minecraft");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("no longer exists");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("permission");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("jail refused it");
    }

    [Fact]
    public async Task WriteFile_MissingPath_IsRefused_WithoutWriting()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                InstanceName: null, ConfigKey: null, ConfigValue: "motd=hi"),
            canPerformActions: true);

        result.Ok.Should().BeFalse();
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("no longer exists");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("permission");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("protected key");
    }

    // --- generalised commands (start/stop/restart/update/backup) ----------------------------

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
        // The server reaches the postcondition, so a verb that has one settles on the first read.
        Fleet("minecraft", CommandSettlement.ExpectedRunning(kind) ?? true);

        var result = await Create().ConfirmAsync(
            new PendingConfirmation(kind, "minecraft"), canPerformActions: true);

        result.Ok.Should().BeTrue();
        result.Summary.Should().Contain("minecraft").And.Contain(pastTense);
        // A verb with a run-state postcondition was OBSERVED to reach it; one without is only ever
        // Accepted, and the two must not blur together.
        result.Verdict.Should().Be(CommandSettlement.ExpectedRunning(kind) is null
            ? ConfirmVerdict.Accepted
            : ConfirmVerdict.Settled);
        await ReceivedOp(kind, "minecraft");
    }

    // --- settlement: the engine accepting a lifecycle command is not the server having done it ----

    [Theory]
    [InlineData(ConfirmationKind.Start)]
    [InlineData(ConfirmationKind.Restart)]
    public async Task Command_AcceptedButNeverReachesRunning_IsNotSettled_AndNotASuccess(ConfirmationKind kind)
    {
        Instances("minecraft");
        StubOp(kind, "minecraft", Result.Success());
        Fleet("minecraft", running: false);   // the engine took the request; the server never came up

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, kind, "minecraft", OpFor(kind), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.NotSettled);
        result.Ok.Should().BeFalse();
        result.ObservedState.Should().Be(ServerRunState.Stopped);
        result.Summary.Should().Contain("still stopped");
    }

    [Fact]
    public async Task Command_StopThatNeverStops_IsNotSettled()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Stop, "minecraft", Result.Success());
        Fleet("minecraft", running: true);   // still up when the window closed

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Stop, "minecraft", OpFor(ConfirmationKind.Stop), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.NotSettled);
        result.ObservedState.Should().Be(ServerRunState.Running);
    }

    [Fact]
    public async Task Command_StateUnreadable_IsUnknown_NeverStopped_AndNeverASuccess()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        FleetUnreadable("minecraft", "the status source is offline");

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming);

        // The distinction this whole type exists for: "we looked and it wasn't running" and "we could
        // not look" are different facts, and neither is a success.
        result.Verdict.Should().Be(ConfirmVerdict.Unknown);
        result.Ok.Should().BeFalse();
        result.ObservedState.Should().Be(ServerRunState.Unknown);
        result.Reason.Should().Contain("offline");
        result.Summary.Should().Contain("unknown");
    }

    [Fact]
    public async Task Command_FleetReadFails_IsUnknown_NotStopped()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<IReadOnlyList<FleetStatusEntry>>("kgsm is unreachable")));

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.Unknown);
        result.Reason.Should().Contain("unreachable");
    }

    [Fact]
    public async Task Command_InstanceMissingFromFleet_IsUnknown_NotStopped()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        Fleet("something-else", running: true);   // our instance isn't in the listing at all

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.Unknown);
        result.ObservedState.Should().Be(ServerRunState.Unknown);
    }

    [Fact]
    public async Task Command_OperationFails_IsFailed_AndNeverObserves()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Failure("kgsm exploded"));

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.Failed);
        result.Summary.Should().Contain("kgsm exploded");
        // A command that never ran has nothing to observe — we must not go looking and then report
        // whatever the server happened to be doing already.
        await _operations.DidNotReceive().GetFleetStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_SettlesOnALaterPoll_NotJustTheFirst()
    {
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());

        // Not up on the first read, up on the second — the ordinary case for a native start, and the
        // reason this is a poll rather than a single check.
        var reads = 0;
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult(Result.Success<IReadOnlyList<FleetStatusEntry>>(
                [new FleetStatusEntry("minecraft", FleetStatusAvailability.Read, ++reads > 1, null)])));

        var result = await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming);

        result.Verdict.Should().Be(ConfirmVerdict.Settled);
        result.ObservedState.Should().Be(ServerRunState.Running);
        reads.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Command_SettlingImmediately_NarratesNothing()
    {
        // The common case settles on the first read. A step announcing a wait that never happened would
        // be narration of nothing, so the sink must stay untouched.
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        Fleet("minecraft", running: true);
        var progress = Substitute.For<ITurnProgress>();

        await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming, progress);

        progress.DidNotReceive().Report(Arg.Any<Tool>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Command_ThatHasToWait_NarratesTheWaitOnce()
    {
        // A real wait is narrated, so a streamed confirm shows movement instead of only heartbeats — and
        // exactly once, however many times it polls.
        Instances("minecraft");
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        Fleet("minecraft", running: false);
        var progress = Substitute.For<ITurnProgress>();

        await CommandSettlement.RunAndSettleAsync(
            _operations, ConfirmationKind.Start, "minecraft", OpFor(ConfirmationKind.Start), FastTiming, progress);

        progress.Received(1).Report(
            Arg.Any<Tool>(), "settling", Arg.Is<string>(s => s.Contains("come up")));
    }

    /// <summary>A window short enough that an unsettled case closes in milliseconds, not 90 seconds.</summary>
    private static readonly SettlementTiming FastTiming =
        new(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10));

    private Func<string, CancellationToken, Task<Result>> OpFor(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Start => _operations.StartAsync,
        ConfirmationKind.Stop => _operations.StopAsync,
        ConfirmationKind.Restart => _operations.RestartAsync,
        ConfirmationKind.Update => _operations.UpdateAsync,
        ConfirmationKind.Backup => _operations.CreateBackupAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Fact]
    public async Task Command_TargetGone_IsRefused_WithoutExecuting()
    {
        Instances(); // the instance vanished since staging (or the token is being replayed)
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Start, "minecraft"), canPerformActions: true);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("no longer exists");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_UnauthorizedCaller_IsRefused_AndNothingExecutes()
    {
        Instances("minecraft");
        var result = await Create().ConfirmAsync(
            new PendingConfirmation(ConfirmationKind.Start, "minecraft"), canPerformActions: false);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("permission");
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

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("kgsm exploded");
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
