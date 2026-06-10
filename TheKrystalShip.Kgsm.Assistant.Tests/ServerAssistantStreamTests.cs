using System.Runtime.CompilerServices;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Streaming counterpart to <see cref="ServerAssistantTests"/>: verifies that
/// <c>RunStreamAsync</c> forwards the agent's token/status events, surfaces destructive ops staged
/// during the turn as <c>Confirmation</c> events AFTER the reply and BEFORE the terminal
/// <c>Final</c>, offers the right tool set per caller, and — critically — that two concurrent
/// streams don't cross-contaminate staged confirmations (the AsyncLocal isolation that lets the
/// confirmation scope stay open across yields on a singleton assistant).
/// </summary>
public class ServerAssistantStreamTests
{
    private readonly ISystemPromptBuilder _prompt = Substitute.For<ISystemPromptBuilder>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();

    private ServerAssistant Create(ILlmAgent agent, IConfirmationContext confirmations)
    {
        _prompt.BuildAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("system");
        return new ServerAssistant(
            agent, _prompt, confirmations, _inventory, _operations, NullLogger<ServerAssistant>.Instance);
    }

    private static async Task<List<AssistantStreamEvent>> DrainAsync(IAsyncEnumerable<AssistantStreamEvent> events)
    {
        var collected = new List<AssistantStreamEvent>();
        await foreach (var ev in events)
            collected.Add(ev);
        return collected;
    }

    [Fact]
    public async Task ForwardsTokensAndStatus_ThenFinal()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[]
        {
            AgentEvent.Status("Running get_server_status…"),
            AgentEvent.Token("All "),
            AgentEvent.Token("good."),
            AgentEvent.Final("All good."),
        });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "status?", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.Status, AssistantEventKind.Token, AssistantEventKind.Token, AssistantEventKind.Final);
        events[^1].Text.Should().Be("All good.");
    }

    [Fact]
    public async Task StagedConfirmation_SurfacesAfterTokens_AndBeforeFinal()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(
            confirmations,
            new[] { AgentEvent.Token("Staging…"), AgentEvent.Final("Staged.") },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "terraria") });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "remove terraria", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.Token, AssistantEventKind.Confirmation, AssistantEventKind.Final);
        events[1].StagedConfirmation!.Kind.Should().Be(ConfirmationKind.Uninstall);
        events[1].StagedConfirmation!.Target.Should().Be("terraria");
    }

    [Fact]
    public async Task ConfirmationStagedAfterAnEarlierYield_StillDrains()
    {
        // Multi-round timing: the op is staged AFTER a Status has already been yielded to the
        // consumer — i.e. after the point where the ambient AsyncLocal would have been lost. The
        // scope-backed drain must still surface it (the dispatcher staged into the list captured
        // at the agent's first await). This locks the residual case the single-round test misses.
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.Status("Running uninstall_server…"),
                AgentEvent.Token("Staging…"),
                AgentEvent.Final("Staged."),
            },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "valheim") },
            stageAfter: 1); // stage only after the first event (the Status) has been yielded out

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "remove valheim", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.Status, AssistantEventKind.Token,
            AssistantEventKind.Confirmation, AssistantEventKind.Final);
        events.Single(e => e.Kind == AssistantEventKind.Confirmation)
            .StagedConfirmation!.Target.Should().Be("valheim");
    }

    [Fact]
    public async Task UnauthorizedCaller_IsOfferedOnlyReadOnlyTools()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[] { AgentEvent.Final("ok") });

        await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "hi", canPerformActions: false));

        agent.LastTurn!.Tools.Should().BeSameAs(LlmTools.ReadOnly);
    }

    [Fact]
    public async Task ErrorEvent_IsTerminal_AndDrainsNoConfirmations()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(
            confirmations,
            new[] { AgentEvent.Token("partial"), AgentEvent.Error("backend down") },
            // Even if something had been staged, an errored turn must not surface confirmations.
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "terraria") });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "x", true));

        events.Select(e => e.Kind).Should().Equal(AssistantEventKind.Token, AssistantEventKind.Error);
        events.Should().NotContain(e => e.Kind == AssistantEventKind.Confirmation);
        events.Should().NotContain(e => e.Kind == AssistantEventKind.Final);
    }

    [Fact]
    public async Task InterleavedStreams_DoNotCrossContaminateStagedConfirmations()
    {
        // ConfirmationContext is a static AsyncLocal shared by every instance — isolation is by
        // async flow, not by instance. Two streams advanced in lock-step must each drain only the
        // op staged within its OWN flow.
        var confirmations = new ConfirmationContext();

        var agentA = new ScriptedAgent(confirmations, new[] { AgentEvent.Final("a") },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "alpha") });
        var agentB = new ScriptedAgent(confirmations, new[] { AgentEvent.Final("b") },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "beta") });

        await using var a = Create(agentA, confirmations).RunStreamAsync("web:a", "x", true).GetAsyncEnumerator();
        await using var b = Create(agentB, confirmations).RunStreamAsync("web:b", "y", true).GetAsyncEnumerator();

        var fromA = new List<AssistantStreamEvent>();
        var fromB = new List<AssistantStreamEvent>();
        bool moreA = true, moreB = true;
        while (moreA || moreB)
        {
            if (moreA && (moreA = await a.MoveNextAsync())) fromA.Add(a.Current);
            if (moreB && (moreB = await b.MoveNextAsync())) fromB.Add(b.Current);
        }

        fromA.Where(e => e.Kind == AssistantEventKind.Confirmation)
            .Select(e => e.StagedConfirmation!.Target).Should().Equal("alpha");
        fromB.Where(e => e.Kind == AssistantEventKind.Confirmation)
            .Select(e => e.StagedConfirmation!.Target).Should().Equal("beta");
    }

    /// <summary>
    /// A stub <see cref="ILlmAgent"/> that yields a scripted event sequence and, during the run,
    /// stages the given destructive ops into the ambient confirmation sink — exactly as the real
    /// dispatcher does inside the agent loop (so <see cref="ServerAssistant"/> drains them after).
    /// </summary>
    private sealed class ScriptedAgent : ILlmAgent
    {
        private readonly IConfirmationContext _confirmations;
        private readonly IReadOnlyList<AgentEvent> _events;
        private readonly IReadOnlyList<PendingConfirmation> _stage;
        private readonly int _stageAfter;

        public AgentTurn? LastTurn { get; private set; }

        /// <param name="stageAfter">
        /// Stage the ops only after this many events have been yielded (0 = up front, before any
        /// yield). A positive value reproduces a staging that happens after the consumer has
        /// already pulled earlier events — the timing where the ambient AsyncLocal would be lost.
        /// </param>
        public ScriptedAgent(
            IConfirmationContext confirmations,
            IReadOnlyList<AgentEvent> events,
            IReadOnlyList<PendingConfirmation>? stage = null,
            int stageAfter = 0)
        {
            _confirmations = confirmations;
            _events = events;
            _stage = stage ?? Array.Empty<PendingConfirmation>();
            _stageAfter = stageAfter;
        }

        public Task<Result<string>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This stub only streams.");

        public async IAsyncEnumerable<AgentEvent> RunStreamAsync(
            AgentTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastTurn = turn;
            // Stage within this flow (the dispatcher would stage here, inside the BeginTurn scope).
            if (_stageAfter == 0)
                Stage();

            for (var i = 0; i < _events.Count; i++)
            {
                await Task.Yield();
                yield return _events[i];
                if (_stageAfter > 0 && i + 1 == _stageAfter)
                    Stage(); // runs on the NEXT MoveNextAsync — i.e. after the consumer pulled this event
            }
        }

        private void Stage()
        {
            foreach (var c in _stage)
                _confirmations.Stage(c);
        }
    }
}
