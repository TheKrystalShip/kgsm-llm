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
/// <c>RunStreamAsync</c> forwards the agent's token/tool events, surfaces destructive ops staged
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
            agent, _prompt, confirmations, _inventory, _operations,
            new NoopToolRelevanceFilter(), NullLogger<ServerAssistant>.Instance);
    }

    private static async Task<List<AssistantStreamEvent>> DrainAsync(IAsyncEnumerable<AssistantStreamEvent> events)
    {
        var collected = new List<AssistantStreamEvent>();
        await foreach (var ev in events)
            collected.Add(ev);
        return collected;
    }

    [Fact]
    public async Task ForwardsToolEventsAndTokens_ThenFinal()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[]
        {
            AgentEvent.ToolStart("get_status", new Dictionary<string, string?>()),
            AgentEvent.ToolResult("get_status", "factorio-test: stopped"),
            AgentEvent.Token("All "),
            AgentEvent.Token("good."),
            AgentEvent.Final("All good."),
        });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "status?", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.ToolStart, AssistantEventKind.ToolResult,
            AssistantEventKind.Token, AssistantEventKind.Token, AssistantEventKind.Final);
        events[0].ToolName.Should().Be("get_status");
        events[1].ToolSummary.Should().Be("factorio-test: stopped");
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
        // Multi-round timing: the op is staged AFTER a tool.start has already been yielded to the
        // consumer — i.e. after the point where the ambient AsyncLocal would have been lost. The
        // scope-backed drain must still surface it (the dispatcher staged into the list captured
        // at the agent's first await). This locks the residual case the single-round test misses.
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.ToolStart("uninstall_server", new Dictionary<string, string?>()),
                AgentEvent.Token("Staging…"),
                AgentEvent.Final("Staged."),
            },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Uninstall, "valheim") },
            stageAfter: 1); // stage only after the first event (the tool.start) has been yielded out

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "remove valheim", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.ToolStart, AssistantEventKind.Token,
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

    // A consumer-set ambient stands in for the Service's IInvocationContext (provenance: who/through-what),
    // which the /turn handler opens BEFORE consuming RunStreamAsync. It must reach the agent-loop flow —
    // that is where the kgsm chokepoint (KgsmServerOperations) reads it to stamp the audit event.
    private static readonly AsyncLocal<string?> _consumerAmbient = new();

    [Fact]
    public async Task ConsumerSetAmbient_FlowsIntoTheAgentLoop_IncludingAfterAYield()
    {
        // The provenance flow under test: the /turn handler sets an AsyncLocal, then (for SSE) awaits
        // SseTurnWriter, which `await foreach`-es RunStreamAsync. RunStreamAsync ferries the turn out
        // through a channel + an inner iterator — the same structure whose yields drop an ambient set
        // INSIDE it (see the confirmation-scope note above). A consumer-set ambient is different: it is
        // captured when ProduceStreamAsync starts (during the consumer's first MoveNextAsync, inside the
        // scope) and must stay visible to the whole agent loop — including reads that happen AFTER an
        // event has been yielded back out to the consumer (where the real tool dispatch reads it).
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.ToolStart("server_command", new Dictionary<string, string?>()),
                AgentEvent.Token("Working…"),
                AgentEvent.Final("Done."),
            },
            // Probe the ambient at the exact flow where KgsmServerOperations.Provenance() reads it:
            // inside the agent loop, on the ProduceStreamAsync flow.
            ambientProbe: () => _consumerAmbient.Value);

        var previous = _consumerAmbient.Value;
        _consumerAmbient.Value = "discord:Haru"; // set by the consumer BEFORE it begins enumerating
        try
        {
            await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "restart it", true));
        }
        finally
        {
            _consumerAmbient.Value = previous; // restore (static AsyncLocal is shared across tests)
        }

        // Up-front AND every post-yield observation must see the consumer's value — never null
        // (the silent-failure mode: a dropped ambient → an unattributed audit row that looks fine).
        agent.AmbientObservations.Should().NotBeEmpty();
        agent.AmbientObservations.Should().OnlyContain(v => v == "discord:Haru");
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
        private readonly Func<string?>? _ambientProbe;

        public AgentTurn? LastTurn { get; private set; }

        /// <summary>
        /// Every value the <c>ambientProbe</c> read, in order: once on entry (a tool dispatched up
        /// front) and once after each yielded event (a dispatch on the resumed flow). Stands in for
        /// each point KgsmServerOperations would read the provenance ambient during the turn.
        /// </summary>
        public List<string?> AmbientObservations { get; } = new();

        /// <param name="stageAfter">
        /// Stage the ops only after this many events have been yielded (0 = up front, before any
        /// yield). A positive value reproduces a staging that happens after the consumer has
        /// already pulled earlier events — the timing where the ambient AsyncLocal would be lost.
        /// </param>
        /// <param name="ambientProbe">
        /// If supplied, read on entry and after each yield, recording into <see cref="AmbientObservations"/> —
        /// reproducing the kgsm chokepoint reading a consumer-set provenance ambient inside the agent loop.
        /// </param>
        public ScriptedAgent(
            IConfirmationContext confirmations,
            IReadOnlyList<AgentEvent> events,
            IReadOnlyList<PendingConfirmation>? stage = null,
            int stageAfter = 0,
            Func<string?>? ambientProbe = null)
        {
            _confirmations = confirmations;
            _events = events;
            _stage = stage ?? Array.Empty<PendingConfirmation>();
            _stageAfter = stageAfter;
            _ambientProbe = ambientProbe;
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
            Probe(); // a tool dispatched up front, before any event is yielded out

            for (var i = 0; i < _events.Count; i++)
            {
                await Task.Yield();
                yield return _events[i];
                Probe(); // a tool dispatched after the consumer pulled this event (post-yield flow)
                if (_stageAfter > 0 && i + 1 == _stageAfter)
                    Stage(); // runs on the NEXT MoveNextAsync — i.e. after the consumer pulled this event
            }
        }

        private void Stage()
        {
            foreach (var c in _stage)
                _confirmations.Stage(c);
        }

        private void Probe()
        {
            if (_ambientProbe is not null)
                AmbientObservations.Add(_ambientProbe());
        }
    }
}
