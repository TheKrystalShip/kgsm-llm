using FluentAssertions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Status;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>
/// Guards the safety-critical confirmation gate (§5 / L8). The single most important property:
/// a non-interactive stdin (piped/scripted) must NEVER auto-execute a staged destructive op.
/// (IServerAssistant / PendingConfirmation / ConfirmationKind resolve via the enclosing
/// TheKrystalShip.Kgsm.Assistant namespace.)
/// </summary>
public class ConfirmationFlowTests
{
    private readonly IServerAssistant _assistant = Substitute.For<IServerAssistant>();
    private readonly IInventoryInvalidation _inventory = Substitute.For<IInventoryInvalidation>();

    private static PendingConfirmation Uninstall(string target = "terraria") =>
        new(ConfirmationKind.Uninstall, target);

    private async Task<(bool ok, string err)> Drain(
        IReadOnlyList<PendingConfirmation> confirmations, bool interactiveStdin, string stdin)
    {
        var err = new StringWriter();
        var ok = await ConfirmationFlow.DrainAsync(
            confirmations, _assistant, _inventory, canPerformActions: true,
            interactiveStdin, color: false, new StringReader(stdin), err, CancellationToken.None);
        return (ok, err.ToString());
    }

    [Fact]
    public async Task NonInteractiveStdin_PrintsProposal_NeverExecutes()   // L8 — the headline safety property
    {
        var (ok, err) = await Drain(new[] { Uninstall() }, interactiveStdin: false, stdin: "");

        await _assistant.DidNotReceive()
            .ConfirmAsync(Arg.Any<PendingConfirmation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _inventory.DidNotReceive().Invalidate();
        err.Should().Contain("uninstall 'terraria'").And.Contain("not run");
        ok.Should().BeTrue();   // the turn answered; the action is simply pending, not a failure
    }

    [Fact]
    public async Task Interactive_Yes_Executes_AndInvalidatesInventory()
    {
        _assistant.ConfirmAsync(Arg.Any<PendingConfirmation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfirmOutcome.Accepted("terraria has been uninstalled", "uninstall", "terraria")));

        var (ok, err) = await Drain(new[] { Uninstall() }, interactiveStdin: true, stdin: "y\n");

        await _assistant.Received(1).ConfirmAsync(
            Arg.Is<PendingConfirmation>(c => c.Target == "terraria"), true, Arg.Any<CancellationToken>());
        _inventory.Received(1).Invalidate();   // L6 — next inventory read is fresh
        err.Should().Contain("terraria has been uninstalled");
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task Interactive_No_Skips_WithoutExecuting()
    {
        var (ok, err) = await Drain(new[] { Uninstall() }, interactiveStdin: true, stdin: "n\n");

        await _assistant.DidNotReceive()
            .ConfirmAsync(Arg.Any<PendingConfirmation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _inventory.DidNotReceive().Invalidate();
        err.Should().Contain("skipped");
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task Interactive_EmptyAnswer_DefaultsToNo()   // [y/N] — default is No
    {
        var (ok, _) = await Drain(new[] { Uninstall() }, interactiveStdin: true, stdin: "\n");

        await _assistant.DidNotReceive()
            .ConfirmAsync(Arg.Any<PendingConfirmation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task Interactive_Yes_FailedAction_ReturnsFalse()
    {
        _assistant.ConfirmAsync(Arg.Any<PendingConfirmation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfirmOutcome.Refused("instance no longer exists")));

        var (ok, err) = await Drain(new[] { Uninstall() }, interactiveStdin: true, stdin: "y\n");

        _inventory.Received(1).Invalidate();
        err.Should().Contain("instance no longer exists");
        ok.Should().BeFalse();
    }
}
