using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies <see cref="ServerAssistant.ConfirmAsync"/> — the model-independent execution
/// gate for staged destructive ops: authority is re-checked, the target is re-validated
/// against live inventory (which also blunts token replay), and only then does it execute.
/// </summary>
public class ServerAssistantConfirmTests
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();

    private ServerAssistant Create() => new(
        Substitute.For<ILlmAgent>(),
        Substitute.For<ISystemPromptBuilder>(),
        new ConfirmationContext(),
        _inventory,
        _operations,
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
}
