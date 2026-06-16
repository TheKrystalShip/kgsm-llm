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
/// single per-message staging cap (every command is propose-only, §3.5), and the
/// unauthorized-caller refusal. The loop itself is the library's concern (and is
/// tested there); here we capture the <see cref="AgentTurn"/> the assistant builds
/// and exercise its gate directly.
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
            _agent, _prompt, _confirmations, _inventory, _operations,
            new NoopToolRelevanceFilter(), NullLogger<ServerAssistant>.Instance);
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
    public async Task UnauthorizedCaller_GateRefusesCommand()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(LlmTools.ServerCommand));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_CapsStagedCommandsAtFivePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var stop = Call(LlmTools.ServerCommand);

        // First five proposed commands are allowed (the dispatcher only STAGES them)...
        for (var i = 0; i < 5; i++)
            turn.Gate!(stop).Allowed.Should().BeTrue($"call {i} should be within the cap");

        // ...the sixth is refused.
        var sixth = turn.Gate!(stop);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("separately");
    }

    [Fact]
    public async Task Gate_OneCap_SpansEveryCommandKind()
    {
        // §3.5: ordinary commands and the formerly-"destructive" ops now share ONE staging
        // cap — a mix of kinds counts together, with no separate budget per tier.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.SetConfigValue)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();

        // Five staged across kinds; the sixth (any kind) is refused.
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_DoesNotCountReadOnlyToolsAgainstTheCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var status = Call(LlmTools.GetStatus);

        // Many read-only calls, all allowed and none consuming the staging budget.
        for (var i = 0; i < 10; i++)
            turn.Gate!(status).Allowed.Should().BeTrue();

        // The staging budget is still fully intact afterwards.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_RefusesAuthorizedReadForUnauthorizedCaller()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var gate = turn.Gate!(Call(LlmTools.ViewConfigFile));

        gate.Allowed.Should().BeFalse();
        gate.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_AllowsAuthorizedReadForAuthorizedCaller_WithoutConsumingStagingCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var view = Call(LlmTools.ViewConfigFile);

        // Many config views are allowed and none consume the staging budget...
        for (var i = 0; i < 10; i++)
            turn.Gate!(view).Allowed.Should().BeTrue();

        // ...so the full staging budget remains.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesDestructive()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeFalse();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_AllowsWebSearchForUnauthorizedCaller_ButCapsItPerMessage()
    {
        // web_search is offered to everyone (read-only tier), so an unauthorized caller may use it —
        // but each call spends a credit, so the gate caps it at three per message (the in-turn
        // runaway-spend guard; the per-day wallet cap is a separate host-side backstop).
        var turn = await CaptureTurnAsync(canPerformActions: false);
        var search = Call(LlmTools.WebSearch);

        for (var i = 0; i < 3; i++)
            turn.Gate!(search).Allowed.Should().BeTrue($"search {i} is within the per-message cap");

        var fourth = turn.Gate!(search);
        fourth.Allowed.Should().BeFalse();
        fourth.RefusalMessage.Should().Contain("web searches per message");
    }

    [Fact]
    public async Task Gate_WebSearchCap_IsSeparateFromTheStagingCap()
    {
        // The two budgets are independent counters: exhausting web searches must not eat into the
        // command-staging budget, and web searches never consume staging slots.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        for (var i = 0; i < 3; i++)
            turn.Gate!(Call(LlmTools.WebSearch)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.WebSearch)).Allowed.Should().BeFalse(); // web cap hit

        // The full staging budget is still intact.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeFalse();
    }
}
