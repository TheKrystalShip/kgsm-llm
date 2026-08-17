using System.Runtime.CompilerServices;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
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

    /// <summary>What the brain recorded about its own conduct this test.</summary>
    protected RecordingAssistantJournal Journal { get; } = new();

    private ServerAssistant Create(ILlmAgent agent, IConfirmationContext confirmations, ITurnProgress? progress = null)
    {
        _prompt.BuildAsync(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new BuiltPrompt("system", "deadbeef"));
        return new ServerAssistant(
            agent, _prompt, confirmations, progress ?? new TurnProgress(), _inventory, _operations,
            new NoopToolRelevanceFilter(), ShippedText.Catalog, Substitute.For<IBlueprintAuthoring>(),
            Options.Create(new SearchOptions { WebEnabled = true }), Options.Create(new FetchOptions { Available = true }),
            Options.Create(new BlueprintAuthoringFlags { Available = true }),
            SettlementTiming.Default, Journal, NullLogger<ServerAssistant>.Instance);
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
            AgentEvent.ToolStart(new Tool("get_status"), new Dictionary<string, string?>()),
            AgentEvent.ToolResult(new Tool("get_status"), "factorio-test: stopped"),
            AgentEvent.Token("All "),
            AgentEvent.Token("good."),
            AgentEvent.Final("All good."),
        });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "status?", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.ToolStart, AssistantEventKind.ToolResult,
            AssistantEventKind.Token, AssistantEventKind.Token, AssistantEventKind.Final);
        events[0].ToolName.Should().Be(new Tool("get_status"));
        events[1].ToolSummary.Should().Be("factorio-test: stopped");
        events[^1].Text.Should().Be("All good.");
    }

    // --- P4: progress narration ----------------------------------------------------------------------

    [Fact]
    public async Task ProgressReportedDuringToolDispatch_LandsInOrder_BeforeThatTool_sResult()
    {
        var confirmations = new ConfirmationContext();
        var progress = new TurnProgress();
        var agent = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.ToolStart(ShippedText.Name(LlmTools.CreateBlueprint), new Dictionary<string, string?> { ["game"] = "Rust" }),
                AgentEvent.ToolResult(ShippedText.Name(LlmTools.CreateBlueprint), "Verified."),
                AgentEvent.Final("Rust is now in the catalog."),
            },
            progress: progress,
            progressSteps: new (Tool, string, string)[]
            {
                (ShippedText.Name(LlmTools.CreateBlueprint), "research", "Looking it up online…"),
                (ShippedText.Name(LlmTools.CreateBlueprint), "draft", "Building a server config…"),
            });

        var events = await DrainAsync(Create(agent, confirmations, progress).RunStreamAsync("web:1", "make me a rust server", true));

        events.Select(e => e.Kind).Should().Equal(
            AssistantEventKind.ToolStart, AssistantEventKind.Progress, AssistantEventKind.Progress,
            AssistantEventKind.ToolResult, AssistantEventKind.Final);
        events.Where(e => e.Kind == AssistantEventKind.Progress)
            .Select(e => e.ProgressKey).Should().Equal("research", "draft");
        events.Where(e => e.Kind == AssistantEventKind.Progress)
            .Should().OnlyContain(e => e.ToolName == ShippedText.Name(LlmTools.CreateBlueprint) && e.ProgressStatus == "active");
    }

    [Fact]
    public async Task InterleavedStreams_DoNotCrossContaminateProgress()
    {
        // TurnProgress's AsyncLocal is static and shared by every instance (same isolation model as
        // ConfirmationContext) — two streams advanced in lock-step must each see only the step
        // reported within its OWN flow.
        var confirmations = new ConfirmationContext();
        var progress = new TurnProgress();

        var agentA = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.ToolStart(ShippedText.Name(LlmTools.CreateBlueprint), new Dictionary<string, string?>()),
                AgentEvent.ToolResult(ShippedText.Name(LlmTools.CreateBlueprint), "a"),
                AgentEvent.Final("a"),
            },
            progress: progress,
            progressSteps: new (Tool, string, string)[] { (ShippedText.Name(LlmTools.CreateBlueprint), "research", "alpha step") });
        var agentB = new ScriptedAgent(
            confirmations,
            new[]
            {
                AgentEvent.ToolStart(ShippedText.Name(LlmTools.CreateBlueprint), new Dictionary<string, string?>()),
                AgentEvent.ToolResult(ShippedText.Name(LlmTools.CreateBlueprint), "b"),
                AgentEvent.Final("b"),
            },
            progress: progress,
            progressSteps: new (Tool, string, string)[] { (ShippedText.Name(LlmTools.CreateBlueprint), "research", "beta step") });

        await using var a = Create(agentA, confirmations, progress).RunStreamAsync("web:a", "x", true).GetAsyncEnumerator();
        await using var b = Create(agentB, confirmations, progress).RunStreamAsync("web:b", "y", true).GetAsyncEnumerator();

        var fromA = new List<AssistantStreamEvent>();
        var fromB = new List<AssistantStreamEvent>();
        bool moreA = true, moreB = true;
        while (moreA || moreB)
        {
            if (moreA && (moreA = await a.MoveNextAsync())) fromA.Add(a.Current);
            if (moreB && (moreB = await b.MoveNextAsync())) fromB.Add(b.Current);
        }

        fromA.Where(e => e.Kind == AssistantEventKind.Progress).Select(e => e.ProgressLabel).Should().Equal("alpha step");
        fromB.Where(e => e.Kind == AssistantEventKind.Progress).Select(e => e.ProgressLabel).Should().Equal("beta step");
    }

    [Fact]
    public async Task ToolResultCard_RidesThrough_AsOpaqueToolData()
    {
        // Phase 2: a card attached upstream (ToolDispatcher → AgentEvent.ToolData) must ride through
        // RunStreamAsync verbatim on the AssistantStreamEvent — the assistant relays it, never inspects
        // it (the Service serialises it as the §5·a tool.result `result`). Same id pairs start↔result.
        var card = new ToolResultCard(
            "run_health_check", Confidence.Confirmed,
            new ResultRef(ResourceKind.Server, "factorio-test"),
            new HealthData(CheckState.Pass, Array.Empty<HealthCheck>(), 1, 1, 0));
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[]
        {
            AgentEvent.ToolResult(new Tool("run_health_check"), "factorio-test: healthy.", "tc_0", card),
            AgentEvent.Final("Healthy."),
        });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "health?", true));

        var toolResult = events.Single(e => e.Kind == AssistantEventKind.ToolResult);
        toolResult.ToolCallId.Should().Be("tc_0");
        toolResult.ToolData.Should().BeSameAs(card);
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
                AgentEvent.ToolStart(new Tool("uninstall_server"), new Dictionary<string, string?>()),
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

        agent.LastTurn!.Tools.Should().BeSameAs(ShippedText.Catalog.ReadOnly);
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
                AgentEvent.ToolStart(new Tool("server_command"), new Dictionary<string, string?>()),
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
    /// <summary>
    /// A streamed reply is already on the client's screen token by token, so a correction that only
    /// reached the Final frame would never be seen by a client that renders the live stream. It is
    /// streamed as a token too, and lands in the final text.
    /// </summary>
    [Fact]
    public async Task AClaimedActionOnADoNothingTurn_IsCorrectedInTheStreamAndTheFinalText()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[]
        {
            AgentEvent.Token("I've staged a backup for Ketchup."),
            AgentEvent.Final("I've staged a backup for Ketchup."),
        });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "back up ketchup", true));

        // No confirmation was produced, so the claim is false and gets corrected.
        events.Should().NotContain(e => e.Kind == AssistantEventKind.Confirmation);
        events.Last().Kind.Should().Be(AssistantEventKind.Final);
        events.Last().Text.Should().Contain("Correction");

        var streamed = string.Concat(
            events.Where(e => e.Kind == AssistantEventKind.Token).Select(e => e.Text));
        streamed.Should().Contain("Correction");
    }

    /// <summary>
    /// The claim is caught where the turn can still do something about it: the first unbacked claim
    /// buys one re-prompt — the model is told, mid-turn, that it called no tool and staged nothing —
    /// and only a second one is corrected and left standing.
    /// </summary>
    [Fact]
    public async Task AnUnbackedClaim_BuysOneRePrompt_ThenTheCorrection()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[] { AgentEvent.Final("done") },
            review: new[] { "I've staged a backup for Ketchup.", "I've staged a backup for Ketchup." });

        await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "back up ketchup", true));

        agent.Verdicts[0].WantsRetry.Should().BeTrue();
        // The nudge restates THIS request, which is what keeps the second attempt on the thing that
        // was asked for rather than on the action the rejected reply invented.
        agent.Verdicts[0].Nudge.Should().Be(UnbackedActionClaim.NudgeFor("back up ketchup"));
        agent.Verdicts[0].Nudge.Should().Contain("back up ketchup");
        agent.Verdicts[0].Amendment.Should().Be(UnbackedActionClaim.RetryNotice);
        agent.Verdicts[1].WantsRetry.Should().BeFalse();
        agent.Verdicts[1].Amendment.Should().Be(UnbackedActionClaim.Correction);
    }

    /// <summary>
    /// The review is one-sided in exactly the way the correction is: a turn that staged something
    /// leaves the reply alone, because the claim it makes is true.
    /// </summary>
    [Fact]
    public async Task AClaimIsAccepted_WhenTheTurnStagedSomething()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[] { AgentEvent.Final("done") },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Backup, "Ketchup") },
            review: new[] { "I've staged a backup for Ketchup." });

        await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "back up ketchup", true));

        agent.Verdicts.Should().ContainSingle().Which.Should().Be(ReplyReview.Accept);
    }

    /// <summary>
    /// A reply the review already corrected is not corrected again by the outer net that catches an
    /// agent which never asked.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCorrectedReply_IsNotCorrectedTwice()
    {
        var confirmations = new ConfirmationContext();
        var claimed = "I've staged a backup for Ketchup." + UnbackedActionClaim.Correction;
        var agent = new ScriptedAgent(confirmations, new[] { AgentEvent.Final(claimed) });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "back up ketchup", true));

        events.Last().Text.Should().Be(claimed);
    }

    [Fact]
    public async Task AClaimedActionIsLeftAloneInTheStream_WhenTheTurnActuallyStagedIt()
    {
        var confirmations = new ConfirmationContext();
        var agent = new ScriptedAgent(confirmations, new[]
            {
                AgentEvent.Token("I've staged a backup for Ketchup."),
                AgentEvent.Final("I've staged a backup for Ketchup."),
            },
            stage: new[] { new PendingConfirmation(ConfirmationKind.Backup, "Ketchup") });

        var events = await DrainAsync(Create(agent, confirmations).RunStreamAsync("web:1", "back up ketchup", true));

        events.Should().Contain(e => e.Kind == AssistantEventKind.Confirmation);
        events.Last().Text.Should().NotContain("Correction");
    }

    private sealed class ScriptedAgent : ILlmAgent
    {
        private readonly IConfirmationContext _confirmations;
        private readonly IReadOnlyList<AgentEvent> _events;
        private readonly IReadOnlyList<PendingConfirmation> _stage;
        private readonly int _stageAfter;
        private readonly Func<string?>? _ambientProbe;
        private readonly ITurnProgress? _progress;
        private readonly IReadOnlyList<(Tool Tool, string Key, string Label)> _progressSteps;
        private readonly IReadOnlyList<string> _review;

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
        /// <param name="progress">
        /// If supplied along with <paramref name="progressSteps"/>, reports each step immediately before
        /// the first scripted <see cref="AgentEventKind.ToolResult"/> is yielded — reproducing a real tool
        /// (e.g. <c>create_blueprint</c>'s aggregator) calling <see cref="ITurnProgress.Report"/> from deep
        /// inside its own execution, BEFORE it returns its terminal result.
        /// </param>
        /// <param name="review">
        /// If supplied, each string is put to the turn's own <see cref="AgentTurn.ReviewReply"/> during
        /// the run — where the real loop asks it, with the confirmation scope still live — and the
        /// verdicts land in <see cref="Verdicts"/>.
        /// </param>
        public ScriptedAgent(
            IConfirmationContext confirmations,
            IReadOnlyList<AgentEvent> events,
            IReadOnlyList<PendingConfirmation>? stage = null,
            int stageAfter = 0,
            Func<string?>? ambientProbe = null,
            ITurnProgress? progress = null,
            IReadOnlyList<(Tool Tool, string Key, string Label)>? progressSteps = null,
            IReadOnlyList<string>? review = null)
        {
            _confirmations = confirmations;
            _events = events;
            _stage = stage ?? Array.Empty<PendingConfirmation>();
            _stageAfter = stageAfter;
            _ambientProbe = ambientProbe;
            _progress = progress;
            _progressSteps = progressSteps ?? Array.Empty<(Tool, string, string)>();
            _review = review ?? Array.Empty<string>();
        }

        /// <summary>The verdict the turn's review returned for each reviewed reply, in order.</summary>
        public List<ReplyReview> Verdicts { get; } = new();

        public Task<Result<AgentRunResult>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This stub only streams.");

        public async IAsyncEnumerable<AgentEvent> RunStreamAsync(
            AgentTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastTurn = turn;
            // Stage within this flow (the dispatcher would stage here, inside the BeginTurn scope).
            if (_stageAfter == 0)
                Stage();
            Probe(); // a tool dispatched up front, before any event is yielded out

            foreach (var reply in _review)
                Verdicts.Add(turn.ReviewReply?.Invoke(reply) ?? ReplyReview.Accept);

            var reportedProgress = false;
            for (var i = 0; i < _events.Count; i++)
            {
                // Report progress steps right before the first ToolResult is yielded — the real tool
                // (running inside the dispatcher's await, before it returns) has already reported them
                // by the time its own result comes back.
                if (!reportedProgress && _events[i].Kind == AgentEventKind.ToolResult)
                {
                    reportedProgress = true;
                    foreach (var step in _progressSteps)
                        _progress?.Report(step.Tool, step.Key, step.Label);
                }

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
