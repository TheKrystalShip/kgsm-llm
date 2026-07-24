using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Verifies the startup orphan sweep (<c>assistant-blueprint-authoring-plan.md</c> step 10's backstop):
/// on startup it uninstalls only instances matching <see cref="Kgsm.Assistant.Blueprints.BlueprintProbeNaming"/>'s
/// reserved prefix, leaves every real user instance untouched, and never lets a read/uninstall failure
/// stop the host from starting.
/// </summary>
public sealed class BlueprintProbeSweepServiceTests
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();

    private BlueprintProbeSweepService Create() =>
        new(_inventory, _operations, NullLogger<BlueprintProbeSweepService>.Instance);

    private Task RunAsync() => Create().SweepOnceAsync(CancellationToken.None);

    [Fact]
    public async Task SweepsOnlyProbeInstances_LeavesRealInstancesUntouched()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["terraria-pvp"] = "terraria",
                ["__bp_probe_rust__"] = "rust",
                ["__bp_probe_valheim__"] = "valheim",
            });
        _operations.UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        await RunAsync();

        await _operations.Received(1).UninstallAsync("__bp_probe_rust__", Arg.Any<CancellationToken>());
        await _operations.Received(1).UninstallAsync("__bp_probe_valheim__", Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().UninstallAsync("terraria-pvp", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoProbes_UninstallsNothing()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["terraria-pvp"] = "terraria" });

        await RunAsync();

        await _operations.DidNotReceiveWithAnyArgs().UninstallAsync(default!, default);
    }

    [Fact]
    public async Task UninstallFailure_IsLoggedAndDoesNotThrow()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["__bp_probe_rust__"] = "rust" });
        _operations.UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("kgsm process explosion"));

        var act = RunAsync;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InventoryReadFailure_IsLoggedAndDoesNotStopTheHost()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, string>>(_ => throw new InvalidOperationException("kgsm unreachable"));

        var act = RunAsync;

        await act.Should().NotThrowAsync();
    }
}
