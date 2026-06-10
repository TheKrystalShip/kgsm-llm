using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the kgsm authorization policy that <see cref="ServerAssistant"/>
/// supplies to the (library) agent loop: which tools are offered per caller, the
/// per-message mutating-action cap, and the unauthorized-caller refusal. The loop
/// itself is the library's concern (and is tested there); here we capture the
/// <see cref="AgentTurn"/> the assistant builds and exercise its gate directly.
/// </summary>
public class ServerAssistantTests
{
    private const string Conversation = "1:2";

    private readonly ILlmAgent _agent = Substitute.For<ILlmAgent>();
    private readonly ISystemPromptBuilder _prompt = Substitute.For<ISystemPromptBuilder>();
    private readonly IConfirmationContext _confirmations = new ConfirmationContext();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();

    private ServerAssistant Create()
    {
        _prompt.BuildAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("system");
        return new ServerAssistant(
            _agent, _prompt, _confirmations, _inventory, _operations, NullLogger<ServerAssistant>.Instance);
    }

    /// <summary>Runs a turn and returns the AgentTurn the assistant handed to the loop.</summary>
    private async Task<AgentTurn> CaptureTurnAsync(bool canPerformActions)
    {
        AgentTurn? captured = null;
        _agent.RunAsync(Arg.Do<AgentTurn>(t => captured = t), Arg.Any<CancellationToken>())
            .Returns(Result.Success("ok"));

        await Create().RunAsync(Conversation, "do it", canPerformActions);

        captured.Should().NotBeNull();
        return captured!;
    }

    private static LlmToolCall Call(string name) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = "terraria" });

    [Fact]
    public async Task AuthorizedCaller_IsOfferedAllTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        turn.Tools.Should().BeSameAs(LlmTools.All);
    }

    [Fact]
    public async Task UnauthorizedCaller_IsOfferedOnlyReadOnlyTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);
        turn.Tools.Should().BeSameAs(LlmTools.ReadOnly);
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesMutatingTool()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(LlmTools.StopServer));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_CapsMutatingActionsAtFivePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var stop = Call(LlmTools.StopServer);

        // First five mutating calls are allowed...
        for (var i = 0; i < 5; i++)
            turn.Gate!(stop).Allowed.Should().BeTrue($"call {i} should be within the cap");

        // ...the sixth is refused.
        var sixth = turn.Gate!(stop);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("limit");
    }

    [Fact]
    public async Task Gate_DoesNotCountReadOnlyToolsAgainstTheCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var status = Call(LlmTools.GetServerStatus);

        // Many read-only calls, all allowed and none consuming the action budget.
        for (var i = 0; i < 10; i++)
            turn.Gate!(status).Allowed.Should().BeTrue();

        // The mutating budget is still fully intact afterwards.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.StopServer)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_CapsStagedDestructiveOpsPerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);

        // Destructive ops pass the gate (the dispatcher only STAGES them) up to the cap of 3...
        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeTrue();

        // ...the fourth is refused so one prompt can't tee up a library-wide shuffle.
        var fourth = turn.Gate!(Call(LlmTools.UninstallServer));
        fourth.Allowed.Should().BeFalse();
        fourth.RefusalMessage.Should().Contain("one at a time");
    }

    [Fact]
    public async Task Gate_DestructiveOps_DoNotConsumeTheMutatingCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);

        // Stage some destructive ops...
        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeTrue();

        // ...the 5-action mutating budget is independent and fully intact.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.StopServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.StopServer)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesDestructive()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeFalse();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeFalse();
    }
}
