using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the kgsm authorization policy that <see cref="ServerAssistant"/>
/// supplies to the (library) agent loop: which tools are offered per caller, the
/// single per-message staging cap (every command is propose-only), and the
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
    private readonly ITurnProgress _progress = Substitute.For<ITurnProgress>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IBlueprintAuthoring _blueprintAuthoring = Substitute.For<IBlueprintAuthoring>();

    /// <summary>What the brain recorded about its own conduct this test.</summary>
    protected RecordingAssistantJournal Journal { get; } = new();

    // Default: search, fetch, AND blueprint authoring are all AVAILABLE, so the offered set is the
    // unfiltered catalog (BeSameAs holds) and the gate's per-message caps are exercisable. Availability
    // tests pass their own.
    private ServerAssistant Create(
        SearchOptions? search = null, FetchOptions? fetch = null, BlueprintAuthoringFlags? blueprint = null)
    {
        _prompt.BuildAsync(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<ReplyStyle>(), Arg.Any<string?>())
            .Returns(new BuiltPrompt("system", "deadbeef"));
        return new ServerAssistant(
            _agent, _prompt, _confirmations, _progress, _inventory, _operations,
            new NoopToolRelevanceFilter(), ShippedText.Catalog, _blueprintAuthoring,
            Options.Create(search ?? new SearchOptions { WebEnabled = true }),
            Options.Create(fetch ?? new FetchOptions { Available = true }),
            Options.Create(blueprint ?? new BlueprintAuthoringFlags { Available = true }),
            SettlementTiming.Default, Journal, NullLogger<ServerAssistant>.Instance);
    }

    /// <summary>Runs a turn and returns the AgentTurn the assistant handed to the loop.</summary>
    private async Task<AgentTurn> CaptureTurnAsync(
        bool canPerformActions, SearchOptions? search = null, FetchOptions? fetch = null,
        BlueprintAuthoringFlags? blueprint = null, string? draft = null)
    {
        AgentTurn? captured = null;
        _agent.RunAsync(Arg.Do<AgentTurn>(t => captured = t), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        await Create(search, fetch, blueprint).RunAsync(Conversation, "do it", canPerformActions, openDraftYaml: draft);

        captured.Should().NotBeNull();
        return captured!;
    }

    [Fact]
    public async Task ReviseBlueprint_IsOffered_WhenADraftIsOpen_AndAuthorized()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true,
            blueprint: new BlueprintAuthoringFlags { Available = true }, draft: "name: tf2\nruntime: native\n");

        turn.Tools.Should().Contain(t => t.Tool == ShippedText.Name(LlmTools.ReviseBlueprint));
        // The open draft's content is injected into this turn's system prompt so the model can revise it.
        turn.SystemPrompt.Should().Contain("name: tf2");
    }

    [Fact]
    public async Task ReviseBlueprint_IsOmitted_WhenNoDraftOpen()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true, blueprint: new BlueprintAuthoringFlags { Available = true });

        turn.Tools.Should().NotContain(t => t.Tool == ShippedText.Name(LlmTools.ReviseBlueprint));
        turn.Tools.Should().BeSameAs(ShippedText.Catalog.All);   // no draft ⇒ the unfiltered catalog reference holds
    }

    [Fact]
    public async Task ReviseBlueprint_IsOmitted_ForUnauthorizedCaller_EvenWithADraft()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false,
            blueprint: new BlueprintAuthoringFlags { Available = true }, draft: "name: tf2\n");

        turn.Tools.Should().NotContain(t => t.Tool == ShippedText.Name(LlmTools.ReviseBlueprint));
    }

    private static LlmToolCall Call(Tool name) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = "terraria" });

    [Fact]
    public async Task AuthorizedCaller_IsOfferedAllTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        turn.Tools.Should().BeSameAs(ShippedText.Catalog.All);
    }

    [Fact]
    public async Task UnauthorizedCaller_IsOfferedOnlyReadOnlyTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);
        turn.Tools.Should().BeSameAs(ShippedText.Catalog.ReadOnly);
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesCommand()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand)));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    /// <summary>
    /// A refusal for want of authority is recorded, because it exists nowhere else.
    /// </summary>
    /// <remarks>
    /// The engine's journal cannot hold this: nothing ran, so from its side nothing happened.
    /// Somebody reaching for an action their tier does not carry currently leaves no trace on the host
    /// at all — the refusal goes back to the model as a tool result and stops there.
    /// </remarks>
    [Fact]
    public async Task UnauthorizedCaller_HasTheRefusalRecorded()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand)));

        Journal.Declines.Should().ContainSingle()
            .Which.Tool.Should().Be(ShippedText.Name(LlmTools.ServerCommand).Name);
    }

    /// <summary>
    /// A blast-radius cap is not a refusal worth recording.
    /// </summary>
    /// <remarks>
    /// The caps fire on ordinary model over-eagerness — a runaway loop, an over-keen refine — and
    /// nobody reached past anything. Recording them would bury the refusals that mean somebody tried
    /// to exceed their permissions under a stream of the model being enthusiastic.
    /// </remarks>
    [Fact]
    public async Task Gate_CapsAreNotRecordedAsRefusals()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var stop = Call(ShippedText.Name(LlmTools.ServerCommand));

        for (var i = 0; i < 6; i++)
            turn.Gate!(stop);

        Journal.Declines.Should().BeEmpty(
            "the sixth call was refused by the per-message cap, which is a loop guard rather than "
            + "somebody reaching past their tier");
    }

    [Fact]
    public async Task Gate_CapsStagedCommandsAtFivePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var stop = Call(ShippedText.Name(LlmTools.ServerCommand));

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
        // Every staged kind shares ONE cap — a mix of them counts together, with no
        // separate budget per tier.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.UninstallServer))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.InstallServer))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.SetConfigValue))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.WriteFile))).Allowed.Should().BeTrue();

        // Five staged across kinds; the sixth (any kind) is refused.
        turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesWriteFile()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(ShippedText.Name(LlmTools.WriteFile)));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_DoesNotCountReadOnlyToolsAgainstTheCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var status = Call(ShippedText.Name(LlmTools.ServerInfo));

        // Many read-only calls, all allowed and none consuming the staging budget.
        for (var i = 0; i < 10; i++)
            turn.Gate!(status).Allowed.Should().BeTrue();

        // The staging budget is still fully intact afterwards.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_RefusesAuthorizedReadForUnauthorizedCaller()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var gate = turn.Gate!(Call(ShippedText.Name(LlmTools.ReadFile)));

        gate.Allowed.Should().BeFalse();
        gate.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_AllowsAuthorizedReadForAuthorizedCaller_WithoutConsumingStagingCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var view = Call(ShippedText.Name(LlmTools.ReadFile));

        // Many file reads are allowed and none consume the staging budget...
        for (var i = 0; i < 10; i++)
            turn.Gate!(view).Allowed.Should().BeTrue();

        // ...so the full staging budget remains.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesDestructive()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(Call(ShippedText.Name(LlmTools.UninstallServer))).Allowed.Should().BeFalse();
        turn.Gate!(Call(ShippedText.Name(LlmTools.InstallServer))).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_AllowsSearchForUnauthorizedCaller_ButCapsItPerMessage()
    {
        // search is offered to everyone (read-only tier), so an unauthorized caller may use it — but
        // each call adds a loop iteration (and a web fallback may spend a credit), so the gate caps it
        // per message (the in-turn runaway guard; the per-day web wallet cap is a host-side backstop).
        var turn = await CaptureTurnAsync(canPerformActions: false);
        var search = Call(ShippedText.Name(LlmTools.Search));

        for (var i = 0; i < 5; i++)
            turn.Gate!(search).Allowed.Should().BeTrue($"search {i} is within the per-message cap");

        var sixth = turn.Gate!(search);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("searches per message");
    }

    private static LlmToolCall SearchFor(string query) =>
        new(ShippedText.Name(LlmTools.Search), new Dictionary<string, string?> { ["query"] = query });

    [Fact]
    public async Task Gate_RefusesARepeatedSearch_WithoutSpendingTheCap()
    {
        // A query already run this message cannot return anything new. Refusing it without charging
        // the cap leaves the budget for searches that could still answer something.
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(SearchFor("PalWorld Difficulty option values")).Allowed.Should().BeTrue();

        var repeat = turn.Gate!(SearchFor("PalWorld Difficulty option values"));
        repeat.Allowed.Should().BeFalse();
        repeat.RefusalMessage.Should().Contain("already searched");

        // Four more distinct queries still fit: the duplicate cost nothing.
        for (var i = 0; i < 4; i++)
            turn.Gate!(SearchFor($"distinct query {i}")).Allowed.Should().BeTrue();
    }

    private static LlmToolCall SearchFor(string query, string scope) =>
        new(ShippedText.Name(LlmTools.Search),
            new Dictionary<string, string?> { ["query"] = query, ["scope"] = scope });

    [Fact]
    public async Task Gate_LetsTheSameWordsGoToTheWebAfterTheDocsDidNotAnswerThem()
    {
        // The one that made "it wasn't in the docs, try online" impossible. Keyed on the query
        // alone, the second call looked like a repeat and was refused — with a message saying a repeat
        // returns the same thing, which is simply untrue of a different source. The model asked, was
        // told it had already searched, and gave up.
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(SearchFor("next Valheim update", "local")).Allowed.Should().BeTrue();

        turn.Gate!(SearchFor("next Valheim update", "web"))
            .Allowed.Should().BeTrue("a different source is a different question");
    }

    [Fact]
    public async Task Gate_StillRefusesTheSameWordsInTheSamePlace()
    {
        // The guard it replaces is still doing its job: asking the same source the same thing twice
        // cannot return anything new, and spends an iteration to learn that.
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(SearchFor("next Valheim update", "web")).Allowed.Should().BeTrue();

        var repeat = turn.Gate!(SearchFor("next Valheim update", "web"));
        repeat.Allowed.Should().BeFalse();
        repeat.RefusalMessage.Should().Contain("already searched");
    }

    [Fact]
    public async Task Gate_TellsTheModelTheWebIsStillOpenWhenItRefusesARepeat()
    {
        // A refusal the model reads as "stop searching" is how a docs-only answer became final. The
        // refusal has to name the move that is still available, or it teaches the wrong lesson.
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(SearchFor("next Valheim update", "local")).Allowed.Should().BeTrue();
        var repeat = turn.Gate!(SearchFor("next Valheim update", "local"));

        repeat.Allowed.Should().BeFalse();
        repeat.RefusalMessage.Should().Contain("scope=\"web\"");
    }

    [Fact]
    public async Task Gate_SeesThroughCosmeticVariation_ButNotRewording()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(SearchFor("PalWorld Difficulty option values")).Allowed.Should().BeTrue();

        // Same words, reordered and repunctuated — one search.
        turn.Gate!(SearchFor("values for the Difficulty option in PalWorld?")).Allowed.Should().BeFalse();

        // A different word set is allowed through even though it asks much the same thing. Refusing a
        // question the model hasn't actually asked is the worse error, so the guard stays literal.
        turn.Gate!(SearchFor("PalWorld Difficulty options")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_DistinctSearches_StillHitTheCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        for (var i = 0; i < 5; i++)
            turn.Gate!(SearchFor($"query number {i}")).Allowed.Should().BeTrue();

        var sixth = turn.Gate!(SearchFor("a sixth different question"));
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("searches per message");
    }

    [Fact]
    public async Task Gate_SearchCap_IsSeparateFromTheStagingCap()
    {
        // The two budgets are independent counters: exhausting searches must not eat into the
        // command-staging budget, and searches never consume staging slots.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.Search))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.Search))).Allowed.Should().BeFalse(); // search cap hit

        // The full staging budget is still intact.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeFalse();
    }

    // --- §D7 search availability: the tool is offered iff a source backs it -----------------------

    [Fact]
    public async Task Search_IsOffered_WhenASourceIsAvailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, search: new SearchOptions { WebEnabled = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.Search));
    }

    [Fact]
    public async Task Search_IsOmitted_WhenNoSourceIsAvailable()
    {
        // Neither RAG nor a web provider configured → search is dropped, but the rest of the
        // read-only catalog is still offered.
        var turn = await CaptureTurnAsync(canPerformActions: false, search: new SearchOptions());
        turn.Tools.Select(t => t.Tool).Should().NotContain(ShippedText.Name(LlmTools.Search));
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.ServerInfo));
    }

    [Fact]
    public async Task RequestingSearch_WhenUnavailable_IsAnInvalidToolError()
    {
        // A client explicitly requesting `search` on a host where it's unavailable gets the honest
        // invalid-tool error (it's genuinely not in this host's catalog), never a silently-dead tool.
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        var result = await Create(new SearchOptions()).RunAsync(
            Conversation, "find docs", canPerformActions: false, requestedTools: ["search"]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid tool");
    }

    // --- fetch_url availability + per-message cap (mirrors the search block above) -----------------

    [Fact]
    public async Task FetchUrl_IsOffered_WhenAvailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, fetch: new FetchOptions { Available = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.FetchUrl));
    }

    [Fact]
    public async Task FetchUrl_IsOmitted_WhenUnavailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, fetch: new FetchOptions { Available = false });
        turn.Tools.Select(t => t.Tool).Should().NotContain(ShippedText.Name(LlmTools.FetchUrl));
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.ServerInfo));
    }

    [Fact]
    public async Task RequestingFetchUrl_WhenUnavailable_IsAnInvalidToolError()
    {
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        var result = await Create(fetch: new FetchOptions { Available = false }).RunAsync(
            Conversation, "read this page", canPerformActions: false, requestedTools: ["fetch_url"]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid tool");
    }

    [Fact]
    public async Task Gate_AllowsFetchForUnauthorizedCaller_ButCapsItPerMessage()
    {
        // fetch_url is offered to everyone (read-only tier), but each call is a real outbound HTTP
        // request against a model/user-influenced URL and adds a loop iteration — capped per message.
        var turn = await CaptureTurnAsync(canPerformActions: false);
        var fetch = Call(ShippedText.Name(LlmTools.FetchUrl));

        for (var i = 0; i < 5; i++)
            turn.Gate!(fetch).Allowed.Should().BeTrue($"fetch {i} is within the per-message cap");

        var sixth = turn.Gate!(fetch);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("fetches per message");
    }

    [Fact]
    public async Task Gate_FetchCap_IsSeparateFromSearchAndStagingCaps()
    {
        // Three independent counters: exhausting fetches must not eat into the search or
        // command-staging budgets, and vice versa.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.FetchUrl))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.FetchUrl))).Allowed.Should().BeFalse(); // fetch cap hit

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.Search))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.Search))).Allowed.Should().BeFalse(); // search cap hit, independently

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeTrue();
        turn.Gate!(Call(ShippedText.Name(LlmTools.ServerCommand))).Allowed.Should().BeFalse(); // staging cap hit, independently
    }

    // --- create_blueprint availability + per-message cap + authorization ----------------------------

    [Fact]
    public async Task CreateBlueprint_IsOffered_WhenAvailable_AndAuthorized()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: true, blueprint: new BlueprintAuthoringFlags { Available = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.CreateBlueprint));
    }

    [Fact]
    public async Task CreateBlueprint_IsOmitted_WhenUnavailable()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: true, blueprint: new BlueprintAuthoringFlags { Available = false });
        turn.Tools.Select(t => t.Tool).Should().NotContain(ShippedText.Name(LlmTools.CreateBlueprint));
        turn.Tools.Select(t => t.Tool).Should().Contain(ShippedText.Name(LlmTools.ServerInfo));
    }

    [Fact]
    public async Task CreateBlueprint_IsOmitted_ForUnauthorizedCaller_EvenWhenAvailable()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: false, blueprint: new BlueprintAuthoringFlags { Available = true });
        turn.Tools.Select(t => t.Tool).Should().NotContain(ShippedText.Name(LlmTools.CreateBlueprint));
    }

    [Fact]
    public async Task Gate_RefusesCreateBlueprint_ForUnauthorizedCaller()
    {
        // Defense in depth: even if an unauthorized caller's request somehow reaches the gate (the
        // tool isn't offered per the test above), the gate itself refuses it too.
        var turn = await CaptureTurnAsync(
            canPerformActions: false, blueprint: new BlueprintAuthoringFlags { Available = true });
        var result = turn.Gate!(Call(ShippedText.Name(LlmTools.CreateBlueprint)));
        result.Allowed.Should().BeFalse();
        result.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_CapsCreateBlueprint_AtOnePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var call = Call(ShippedText.Name(LlmTools.CreateBlueprint));

        turn.Gate!(call).Allowed.Should().BeTrue();

        var second = turn.Gate!(call);
        second.Allowed.Should().BeFalse();
        second.RefusalMessage.Should().Contain("blueprint");
    }

    // --- P4: the buffered (non-SSE) path never narrates progress ------------------------------------

    [Fact]
    public async Task RunAsync_NeverOpensAProgressScope()
    {
        // The buffered RunAsync path has no channel to write progress frames onto — unlike
        // RunStreamAsync's ProduceStreamAsync, it must never open the ambient sink, so a long tool
        // (create_blueprint) still runs to completion and returns its one terminal card, with no
        // intermediate narration attempted.
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("Verified.", null)));

        var result = await Create().RunAsync(Conversation, "make me a rust server", canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        _progress.DidNotReceiveWithAnyArgs().BeginTurn(default!);
    }

    // --- the reply is held against what the turn actually did --------------------------------------

    /// <summary>Runs a turn whose model reply is <paramref name="reply"/>, letting
    /// <paramref name="duringTurn"/> record whatever the dispatcher would have recorded.</summary>
    private async Task<string> ReplyAfterTurnAsync(
        string reply, Action? duringTurn = null, bool autoExecute = false)
    {
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                duringTurn?.Invoke();   // inside the live confirmation scope, exactly as the dispatcher is
                return Result.Success(new AgentRunResult(reply, null));
            });

        var result = await Create().RunAsync(
            Conversation, "back up ketchup", canPerformActions: true, autoExecute: autoExecute);

        result.IsSuccess.Should().BeTrue();
        return result.Text!;
    }

    /// <summary>
    /// The model occasionally answers a mutating request conversationally — no tool call — and then
    /// reports the action as staged. Nothing can execute (nothing was staged), but the user is told
    /// to expect a confirmation prompt that was never posted, so the reply is corrected.
    /// </summary>
    [Fact]
    public async Task AClaimedActionIsCorrected_WhenTheTurnStagedAndRanNothing()
    {
        var text = await ReplyAfterTurnAsync("I've staged a backup for Ketchup. Just confirm it on your end.");

        text.Should().Contain("Correction");
        text.Should().Contain("nothing was actually staged");
        // The original is kept, not discarded — a reply may carry real content beside the false claim.
        text.Should().StartWith("I've staged a backup for Ketchup.");
    }

    [Fact]
    public async Task AClaimedActionIsLeftAlone_WhenTheTurnActuallyStagedIt()
    {
        var text = await ReplyAfterTurnAsync(
            "I've staged a backup for Ketchup. Just confirm it on your end.",
            duringTurn: () => _confirmations.Stage(new PendingConfirmation(ConfirmationKind.Backup, "Ketchup")));

        text.Should().NotContain("Correction");
    }

    /// <summary>
    /// The auto-accept path RUNS the command and stages nothing, so "I've backed it up" is true
    /// there. Correcting it would be the same fabrication in the other direction.
    /// </summary>
    [Fact]
    public async Task AClaimedActionIsLeftAlone_OnAnAutoAcceptTurnThatRanIt()
    {
        var text = await ReplyAfterTurnAsync(
            "I've backed up Ketchup — done.",
            duringTurn: () => _confirmations.NoteActionPerformed(),
            autoExecute: true);

        text.Should().NotContain("Correction");
    }

    [Fact]
    public async Task AnHonestReplyIsNeverTouched()
    {
        var text = await ReplyAfterTurnAsync(
            "I can't find a server called Ketchup — check the name?");

        text.Should().NotContain("Correction");
    }

    /// <summary>
    /// A corrected claim is recorded, because nothing else on the host can see it.
    /// </summary>
    /// <remarks>
    /// The correction reaches the person who was talking to it and nobody else. This is the only
    /// measurement of the deployed model's fabrication rate on real prompts — the benchmark scores the
    /// same check against a fixed corpus, which is a different question from what the shipped prompt
    /// does with what people actually ask.
    /// </remarks>
    [Fact]
    public async Task ACorrectedClaimIsRecorded()
    {
        await ReplyAfterTurnAsync("I've staged a backup for Ketchup. Just confirm it on your end.");

        Journal.Claims.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RecordingAssistantJournal.Claim(
                ClaimCheck.UnbackedAction, ClaimResolution.Corrected, ClaimNet.Outer, Conversation));
    }

    /// <summary>
    /// The record carries no transcript.
    /// </summary>
    /// <remarks>
    /// The journal is readable by anything on the host that can open the directory, and what somebody
    /// said to the assistant is theirs. The conversation id is enough to find it; the words are not
    /// this record's to hold.
    /// </remarks>
    [Fact]
    public async Task ACorrectedClaimRecordsNoTranscript()
    {
        const string reply = "I've staged a backup for Ketchup. Just confirm it on your end.";

        await ReplyAfterTurnAsync(reply);

        string recorded = string.Join(" ", Journal.Claims.Select(c => $"{c.Check} {c.Resolution} {c.Net} {c.ConversationId}"));
        recorded.Should().NotContain("Ketchup");
        recorded.Should().NotContain("back up");
    }

    /// <summary>An honest turn records nothing — the count is a measurement, not a heartbeat.</summary>
    [Fact]
    public async Task AnHonestReplyRecordsNothing()
    {
        await ReplyAfterTurnAsync("I can't find a server called Ketchup — check the name?");

        Journal.Claims.Should().BeEmpty();
    }
}
