using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Assembles a kgsm-policy-bearing <see cref="AgentTurn"/> and runs it through the
/// reusable library agent loop (<see cref="ILlmAgent"/>).
///
/// This is where ALL kgsm authorization policy lives:
///  - which tools are offered (read-only for everyone, authorized-read + commands only for authorized callers);
///  - the per-message blast-radius cap on staged commands;
///  - the defense-in-depth refusal of a command (or authorized read) from an unauthorized caller;
///  - draining the commands the dispatcher staged this turn so the caller can confirm them.
/// The library loop knows none of this — it just evaluates the gate we supply.
/// </summary>
public class ServerAssistant : IServerAssistant
{
    /// <summary>
    /// Blast-radius limit: at most this many commands may be staged (proposed) per user
    /// message. Every command is propose-only and needs a per-op human
    /// confirmation, but this still stops one prompt from teeing up a fleet-wide shuffle
    /// of confirmation buttons. Tunable; kept small on purpose.
    /// </summary>
    private const int MaxStagedCommandsPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on <c>search</c> calls. Each adds an agent-loop iteration (and a web
    /// fallback may spend a provider credit), so this stops one prompt from spraying lookups (a
    /// runaway loop or an over-eager refine). It is now a loop-runaway guard, not the wallet guard —
    /// the per-day web spend cap lives host-side in the provider. Matches the staging cap; tunable.
    /// </summary>
    private const int MaxSearchesPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on <c>fetch_url</c> calls — mirrors <see cref="MaxSearchesPerMessage"/> for
    /// the same reason (a loop-runaway guard; the per-day fetch wallet cap lives host-side in the
    /// adapter). Each fetch is a real outbound HTTP request against a model/user-influenced URL, so
    /// this stops one prompt from spraying page reads.
    /// </summary>
    private const int MaxFetchesPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on <c>create_blueprint</c> calls. Kept at 1 — each run is a real research +
    /// test-install + teardown pipeline (far heavier than a search or fetch), so one message proposing
    /// several new games at once is refused rather than fanning out several probes concurrently.
    /// </summary>
    private const int MaxBlueprintAuthoringsPerMessage = 1;

    /// <summary>
    /// Per-message ceiling on memories written. A turn that settles one thing worth keeping is the
    /// normal case and three is generous; past that it is a model filling somebody's whole store from a
    /// single message, which spends the per-owner cap on one conversation's worth of detail.
    /// </summary>
    private const int MaxMemoryWritesPerMessage = 3;

    private readonly ILlmAgent _agent;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IConfirmationContext _confirmations;
    private readonly ITurnProgress _progress;
    private readonly IServerInventory _inventory;
    private readonly IServerOperations _operations;
    private readonly IToolRelevanceFilter _toolFilter;
    private readonly IToolCatalog _toolCatalog;
    private readonly IBlueprintAuthoring _blueprintAuthoring;
    private readonly SearchOptions _searchOptions;
    private readonly FetchOptions _fetchOptions;
    private readonly BlueprintAuthoringFlags _blueprintAuthoringFlags;
    private readonly SettlementTiming _settlement;
    private readonly IAssistantJournal _journal;
    private readonly ILogger<ServerAssistant> _logger;

    public ServerAssistant(
        ILlmAgent agent,
        ISystemPromptBuilder promptBuilder,
        IConfirmationContext confirmations,
        ITurnProgress progress,
        IServerInventory inventory,
        IServerOperations operations,
        IToolRelevanceFilter toolFilter,
        IToolCatalog toolCatalog,
        IBlueprintAuthoring blueprintAuthoring,
        IOptions<SearchOptions> searchOptions,
        IOptions<FetchOptions> fetchOptions,
        IOptions<BlueprintAuthoringFlags> blueprintAuthoringFlags,
        SettlementTiming settlement,
        IAssistantJournal journal,
        ILogger<ServerAssistant> logger)
    {
        _agent = agent;
        _promptBuilder = promptBuilder;
        _confirmations = confirmations;
        _progress = progress;
        _inventory = inventory;
        _operations = operations;
        _toolFilter = toolFilter;
        _toolCatalog = toolCatalog;
        _blueprintAuthoring = blueprintAuthoring;
        _searchOptions = searchOptions.Value;
        _fetchOptions = fetchOptions.Value;
        _blueprintAuthoringFlags = blueprintAuthoringFlags.Value;
        _settlement = settlement;
        _journal = journal;
        _logger = logger;
    }

    /// <summary>A turn-specific context block appended to the system prompt when the user is reviewing an
    /// open blueprint draft: the draft's CURRENT content (edits included — the SPA sends what's in the
    /// editor) plus the instruction to change it only via revise_blueprint. This is what lets the model
    /// actually revise a draft — it cannot see the editor otherwise, and tool results aren't replayed into
    /// later turns' context.</summary>
    private static string OpenDraftContext(string yaml) =>
        "\n\nThe user is currently reviewing an OPEN, unsaved blueprint draft in the editor. Its current " +
        "content is:\n```yaml\n" + yaml.Trim() + "\n```\nIf they ask to change, populate, fix, or add " +
        "anything to it, call revise_blueprint with the COMPLETE updated YAML (this exact content with only " +
        "the requested change applied — research values first if needed). That is the ONLY way to edit the " +
        "draft; never claim you changed it unless revise_blueprint succeeded. If they just ask what it " +
        "contains, answer from the content above.";

    /// <summary>
    /// Two-axis tool selection: authorization picks the set the caller MAY
    /// use (read-only vs all), then client-requested subset may narrow it further,
    /// then the relevance seam may narrow it (no-op today).
    ///
    /// When <paramref name="requestedTools"/> is provided, every requested name is
    /// validated against the server's own catalog. Unknown names cause a hard error;
    /// names the caller isn't authorized for are silently removed (no information
    /// disclosure). If all requested names are unknown, the full authorized set is
    /// NOT returned — the error propagates to the caller.
    /// </summary>
    private Result<IReadOnlyList<LlmToolDefinition>> SelectTools(
        string userPrompt, bool canPerformActions, IReadOnlyList<string>? requestedTools, bool draftOpen = false,
        string? leaf = null)
    {
        var authorized = canPerformActions ? _toolCatalog.All : _toolCatalog.ReadOnly;

        // revise_blueprint is kept OUT of the default catalog (so All keeps its unfiltered-reference
        // identity and the model can't revise nothing). It's APPENDED only for an authorized caller on a
        // turn that actually carries an open draft, with authoring enabled — the one situation where there
        // is a draft to change and its content is injected into this turn's context.
        if (draftOpen && canPerformActions && _blueprintAuthoringFlags.Available)
            authorized = authorized.Append(_toolCatalog.ReviseBlueprintTool).ToArray();

        // Omit-when-disabled: the unified `search` tool is offered only when at least one source
        // backs it (RAG enabled and/or a web provider configured). Removed BEFORE the requested-tool
        // validation below, so a client asking for `search` on a host where it's unavailable gets the
        // honest invalid-tool error — never a dead tool the model would call and watch fail.
        if (!_searchOptions.Available)
            authorized = authorized.Where(t => t.Tool != _toolCatalog.NameOf(LlmTools.Search)).ToArray();

        // Same omit-when-disabled rule for fetch_url: offered only when a real IWebFetch adapter
        // is enabled on this host (FetchOptions.Available).
        if (!_fetchOptions.Available)
            authorized = authorized.Where(t => t.Tool != _toolCatalog.NameOf(LlmTools.FetchUrl)).ToArray();

        // Same omit-when-disabled rule for create_blueprint: offered only when the real authoring
        // pipeline is enabled on this host (BlueprintAuthoringFlags.Available, false everywhere by default).
        if (!_blueprintAuthoringFlags.Available)
            authorized = authorized.Where(t => t.Tool != _toolCatalog.NameOf(LlmTools.CreateBlueprint)).ToArray();

        if (requestedTools is { Count: > 0 })
        {
            // Build the valid-name lookup from the SERVER's catalog — the trust boundary.
            var validTools = authorized.Select(t => t.Tool).ToHashSet();

            // Convert client strings to Tool instances, validating each.
            var requestedToolInstances = new List<Tool>();
            var invalidNames = new List<string>();

            foreach (var name in requestedTools)
            {
                var tool = new Tool(name);
                if (validTools.Contains(tool))
                    requestedToolInstances.Add(tool);
                else
                    invalidNames.Add(name);
            }

            // Hard reject if any requested name is not in the server catalog.
            if (invalidNames.Count > 0)
                return Result.Failure<IReadOnlyList<LlmToolDefinition>>(
                    $"Invalid tool(s): {string.Join(", ", invalidNames)}. " +
                    $"Valid tools: {string.Join(", ", validTools.Select(t => t.Name))}.");

            // Intersect: keep only requested tools that are in the authorized set.
            // Silently removes unauthorized tools (no information disclosure).
            authorized = authorized
                .Where(t => requestedToolInstances.Contains(t.Tool))
                .ToArray();
        }

        // The prose the model routes on already came off disk with the catalog, so there is nothing
        // to overlay here — one file, read once, is the whole story.
        return Result.Success(_toolFilter.GetToolsFor(
            new ToolSelectionContext(userPrompt, canPerformActions), authorized));
    }

    public async Task<AssistantResult> RunAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default,
        string? openDraftYaml = null,
        string? userDisplay = null,
        string? leaf = null,
        bool sharedConversation = false,
        ReplyStyle style = ReplyStyle.Default)
    {
        var draftOpen = !string.IsNullOrWhiteSpace(openDraftYaml);
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools, draftOpen, leaf);
        if (toolResult.IsFailure)
            return AssistantResult.Fail(toolResult.Error!);

        // The style reaches the prompt and stops there. It is never consulted when selecting tools or
        // building the gate: a spoken turn may do exactly what a typed one may.
        var prompt = await _promptBuilder.BuildAsync(
            canPerformActions, autoExecute, cancellationToken, leaf, style,
            MemoryScope.OwnerOf(conversationId));
        var tools = toolResult.Value!;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = draftOpen ? prompt.Text + OpenDraftContext(openDraftYaml!) : prompt.Text,
            SystemPromptHash = prompt.TemplateHash,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
            Think = think,
            UserDisplay = userDisplay,
            // Named only for a conversation people share. In a one-person conversation the label is
            // noise the model has to read past on every single turn, and "you" was never ambiguous.
            Speaker = sharedConversation ? userDisplay : null,
        };

        // The dispatcher stages any proposed commands into this per-turn scope; we drain
        // them after the run so the caller can post confirmation prompts. On an auto-accept
        // turn the dispatcher runs lifecycle verbs immediately instead (scope reads back AutoExecute).
        using var scope = _confirmations.BeginTurn(autoExecute);

        // Somebody who said "look it up online" has stated where they want the answer from. Recorded
        // for the turn rather than left to the model to pass along, because the model was measured
        // not passing it — see SearchIntent.
        using var looking = SearchIntent.BeginTurn(SearchIntent.From(userPrompt));

        // Whose memory this turn may read and write, derived from the conversation rather than named
        // by the model — see MemoryOwner.
        using var remembering = MemoryOwner.BeginTurn(MemoryScope.OwnerOf(conversationId));

        // Every figure the turn is given, for the reply review. Seeded with what the model may quote
        // before any tool has run — the request, and the prompt carrying the instance list and clock.
        using var figures = MeasuredValues.BeginTurn(userPrompt, turn.SystemPrompt);

        // The review reads the scope, so it is attached here rather than at construction: the scope
        // exists only for the duration of the turn.
        var result = await _agent.RunAsync(
            turn with { ReviewReply = BuildReplyReview(scope, conversationId, userPrompt) }, cancellationToken);
        var confirmations = scope.Staged;

        return result.IsSuccess
            ? AssistantResult.Ok(
                NotePendingConfirmation(
                    CorrectUnbackedClaim(result.Value!.Text, scope, conversationId), scope, conversationId, style),
                confirmations,
                result.Value!.Usage)
            : AssistantResult.Fail(result.Error!);
    }

    public async IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? openDraftYaml = null,
        string? userDisplay = null,
        string? leaf = null,
        bool sharedConversation = false,
        ReplyStyle style = ReplyStyle.Default)
    {
        var draftOpen = !string.IsNullOrWhiteSpace(openDraftYaml);
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools, draftOpen, leaf);
        if (toolResult.IsFailure)
        {
            yield return AssistantStreamEvent.Error(toolResult.Error!);
            yield break;
        }

        // Presentation only — see RunAsync: the style reaches the prompt and nothing else.
        var prompt = await _promptBuilder.BuildAsync(
            canPerformActions, autoExecute, cancellationToken, leaf, style,
            MemoryScope.OwnerOf(conversationId));
        var tools = toolResult.Value!;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = draftOpen ? prompt.Text + OpenDraftContext(openDraftYaml!) : prompt.Text,
            SystemPromptHash = prompt.TemplateHash,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
            Think = think,
            UserDisplay = userDisplay,
            // Named only for a conversation people share. In a one-person conversation the label is
            // noise the model has to read past on every single turn, and "you" was never ambiguous.
            Speaker = sharedConversation ? userDisplay : null,
        };

        // CRUCIAL: the dispatcher stages destructive ops into an AsyncLocal confirmation scope
        // DURING the agent run, and that ambient value does NOT survive the `yield return`s an
        // async iterator hands to its consumer. So we run the whole turn on a single, yield-free
        // async flow (Produce — structurally identical to the buffered RunAsync, which is why its
        // scope read is reliable) and ferry events out through a channel. This iterator only
        // relays the channel; its own yields never touch the confirmation scope.
        // SingleWriter is false: a long-running tool (create_blueprint) reports progress steps via
        // ITurnProgress from DEEP inside its own execution — a call site nested under the producer's
        // `await DispatchRoundAsync` in the generic agent loop, on whatever thread the dispatch task
        // resumes on. Those writes and the producer's own mapped-event writes are no longer provably
        // the same writer, so the single-writer fast path is unsafe; System.Threading.Channels supports
        // multiple concurrent writers on an unbounded channel without any extra synchronisation here.
        // Progress writes still happen-before the tool's own `tool.result` write (the aggregator
        // reports and returns before the dispatcher's ExecuteAsync call completes), so ordering holds.
        var channel = Channel.CreateUnbounded<AssistantStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var producer = ProduceStreamAsync(turn, autoExecute, style, channel.Writer, cancellationToken);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken))
                yield return ev;
        }
        finally
        {
            // Observe the producer (surfaces cancellation/faults; it shares the same token, so a
            // cancelled consumer cancels it too). No deadlock: the channel is unbounded.
            await producer;
        }
    }

    /// <summary>
    /// Runs the full agent turn yield-free and writes the mapped events to <paramref name="writer"/>,
    /// draining the staged confirmations after the loop. Being a normal async method (no
    /// <c>yield</c>s to a consumer) keeps the AsyncLocal confirmation scope intact for every
    /// staging the dispatcher does mid-run — the same property the buffered <see cref="RunAsync"/>
    /// relies on.
    /// </summary>
    private async Task ProduceStreamAsync(
        AgentTurn turn,
        bool autoExecute,
        ReplyStyle style,
        ChannelWriter<AssistantStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _confirmations.BeginTurn(autoExecute);
            // The review reads the scope, so it is attached here rather than where the turn is built:
            // the scope exists only for the duration of the turn.
            turn = turn with
            {
                ReviewReply = BuildReplyReview(scope, turn.ConversationId, turn.UserPrompt),
            };
            // Opens the SAME per-turn ambient pattern as the confirmation scope above, carrying the
            // SAME writer this method already relays mapped agent events into — a long tool reports a
            // step by writing straight onto it (see ITurnProgress), landing on the stream immediately
            // instead of waiting for its own terminal tool.result.
            using var progressScope = _progress.BeginTurn(writer);
            // ⚠ Opened HERE and not in the iterator above, for the same reason as the two scopes
            // beside it: an async iterator's yields drop the ambient value, and this method is the
            // yield-free flow the dispatcher actually runs on. Opened in the iterator it would be
            // gone by the first tool call — which is the streaming path every surface uses.
            using var looking = SearchIntent.BeginTurn(SearchIntent.From(turn.UserPrompt));
            // ⚠ Opened HERE for exactly the reason above: this is the yield-free flow the dispatcher
            // runs on, and a memory scope opened in the iterator would be gone by the first tool call.
            using var remembering = MemoryOwner.BeginTurn(MemoryScope.OwnerOf(turn.ConversationId));
            // Opened HERE for the same reason as the scopes above — this is the yield-free flow the
            // dispatcher notes each tool's answer onto.
            using var figures = MeasuredValues.BeginTurn(turn.UserPrompt, turn.SystemPrompt);

            var finalText = string.Empty;
            LlmUsage? finalUsage = null;
            // The id the loop recorded this turn under, carried out to the surface so a client can rate
            // the answer it just received. 0 when the turn was not persisted.
            long finalTurnId = 0;
            var errored = false;

            await foreach (var ev in _agent.RunStreamAsync(turn, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                switch (ev.Kind)
                {
                    case AgentEventKind.Token:
                        await writer.WriteAsync(AssistantStreamEvent.Token(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.Thinking:
                        await writer.WriteAsync(AssistantStreamEvent.Thinking(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.ToolStart:
                        if (ev.ToolName is not null)
                            await writer.WriteAsync(
                                AssistantStreamEvent.ToolStart(
                                    ev.ToolName,
                                    ev.ToolArguments ?? new Dictionary<string, string?>(),
                                    ev.ToolCallId),
                                cancellationToken);
                        break;
                    case AgentEventKind.ToolResult:
                        if (ev.ToolName is not null)
                            await writer.WriteAsync(
                                // Forward the opaque card (ev.ToolData) through verbatim — the domain layer
                                // produced it (ToolDispatcher) and the Service serialises it; we just relay.
                                AssistantStreamEvent.ToolResult(
                                    ev.ToolName, ev.ToolSummary ?? string.Empty, ev.ToolCallId, ev.ToolData),
                                cancellationToken);
                        break;
                    case AgentEventKind.Final:
                        finalText = ev.Text ?? string.Empty;
                        finalUsage = ev.Usage;
                        finalTurnId = ev.TurnId;
                        break;
                    case AgentEventKind.Error:
                        // Terminal: a failed turn stages nothing to confirm.
                        await writer.WriteAsync(
                            AssistantStreamEvent.Error(ev.ErrorMessage ?? "The assistant failed."), cancellationToken);
                        errored = true;
                        break;
                }

                if (errored)
                    break;
            }

            if (!errored)
            {
                // The scope's list is intact (no consumer yields disturbed this flow) — drain it.
                foreach (var confirmation in scope.Staged)
                    await writer.WriteAsync(AssistantStreamEvent.Confirmation(confirmation), cancellationToken);

                var corrected = CorrectUnbackedClaim(finalText, scope, turn.ConversationId);
                if (!ReferenceEquals(corrected, finalText))
                {
                    // The false sentence has already been streamed to the client token by token, so
                    // the correction is streamed too — a client that renders the live tokens and
                    // never re-reads the final text still sees it.
                    await writer.WriteAsync(
                        AssistantStreamEvent.Token(UnbackedActionClaim.Correction), cancellationToken);
                    finalText = corrected;
                }

                // Same reasoning in the other direction: a client rendering live tokens must see the
                // sentence naming what those confirmation prompts are for, not only the final text.
                var noted = NotePendingConfirmation(finalText, scope, turn.ConversationId, style);
                if (!ReferenceEquals(noted, finalText))
                {
                    await writer.WriteAsync(
                        AssistantStreamEvent.Token(PendingConfirmationNote.For(scope.Staged.Count, style)),
                        cancellationToken);
                    finalText = noted;
                }

                await writer.WriteAsync(
                    AssistantStreamEvent.Final(finalText, finalUsage, finalTurnId), cancellationToken);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Holds the model's narration of the turn against what the turn actually did, and appends a
    /// correction when it claims an action that never happened.
    /// <para>
    /// The propose-only design means such a claim can never move a server — nothing was staged, so
    /// there is nothing to confirm. What it does is misinform: the user is told to expect a
    /// confirmation prompt that was never posted, or that a server was stopped when it is running.
    /// That is a fabricated status, and the rule against those does not stop at metrics. The check
    /// is deliberately one-sided — it runs ONLY on a turn that staged nothing and ran nothing, where
    /// any such claim is false by construction, so it can never contradict a real action.
    /// </para>
    /// <para>
    /// This is the outer net, over the reply the turn actually ends on:
    /// <see cref="BuildReplyReview"/> reaches the same claim earlier, where the turn can still be
    /// re-prompted, and its correction carries into the reply here — which is why a reply already
    /// carrying the correction is left alone rather than corrected twice.
    /// </para>
    /// </summary>
    private string CorrectUnbackedClaim(string text, IConfirmationScope scope, string conversationId)
    {
        if (scope.Staged.Count > 0 || scope.ActionPerformed)
            return text;

        if (!UnbackedActionClaim.IsPresentIn(text) || UnbackedActionClaim.CorrectionIsPresentIn(text))
            return text;

        _logger.LogWarning(
            "Reply claimed an action on a turn that staged and ran nothing; correction appended. "
            + "Conversation {ConversationId}", conversationId);

        _journal.ClaimCorrected(
            ClaimCheck.UnbackedAction, ClaimResolution.Corrected,
            ClaimNet.Outer, conversationId);

        return text + UnbackedActionClaim.Correction;
    }

    /// <summary>
    /// The same check as <see cref="CorrectUnbackedClaim"/>, put where the turn can still act on it:
    /// the agent loop asks this of each candidate reply before recording the turn, so an unbacked
    /// claim first buys a second attempt — the model is told, in the same turn, that it called no tool
    /// and that nothing is staged — and only a second one is corrected and left standing.
    /// <para>
    /// A re-prompt is worth taking because the request usually WAS an action the user wanted: the
    /// correction alone is honest but leaves them to ask again, and asking again in the same
    /// conversation meets the same transcript. The verdict is never a guess — it reads the same
    /// per-turn record the confirmation prompts come from.
    /// </para>
    /// </summary>
    private Func<string, ReplyReview> BuildReplyReview(
        IConfirmationScope scope, string conversationId, string userPrompt)
    {
        var rePrompted = false;
        var rePromptedForSearch = false;
        var rePromptedForFigures = false;
        return text =>
        {
            // Asked to look online, and nothing was looked up. Checked before the action claim: this
            // is about whether the work was DONE, where that one is about how it was described, and a
            // turn that never searched has nothing yet worth reviewing the wording of.
            if (SearchIntent.Required == SearchScope.Web && !SearchIntent.AnythingSearched)
            {
                if (!rePromptedForSearch)
                {
                    rePromptedForSearch = true;
                    _logger.LogWarning(
                        "The user asked for the web and the turn called no tool; re-prompting once. "
                        + "Conversation {ConversationId}", conversationId);
                    _journal.ClaimCorrected(
                        ClaimCheck.UnsearchedWeb, ClaimResolution.RePrompted,
                        ClaimNet.Review, conversationId);
                    return ReplyReview.Retry(
                        UnsearchedWebRequest.NudgeFor(userPrompt), UnsearchedWebRequest.RetryNotice);
                }

                _logger.LogWarning(
                    "The user asked for the web and the turn still searched nothing; note appended. "
                    + "Conversation {ConversationId}", conversationId);
                _journal.ClaimCorrected(
                    ClaimCheck.UnsearchedWeb, ClaimResolution.Corrected,
                    ClaimNet.Review, conversationId);
                return ReplyReview.Amend(UnsearchedWebRequest.Correction);
            }

            // A figure the tools did not report. Checked on any turn that called one, whether or not it
            // also staged something: a reply can stage a real backup and still misquote the port beside
            // it, and the misquote is what the reader has no way to catch.
            if (MeasuredValues.AnyToolReported)
            {
                var unbacked = FabricatedFigureClaim.UnbackedIn(text, MeasuredValues.Given);
                if (unbacked.Count > 0)
                {
                    if (!rePromptedForFigures)
                    {
                        rePromptedForFigures = true;
                        _logger.LogWarning(
                            "Reply reported figure(s) {Figures} that no tool returned this turn; re-prompting "
                            + "once. Conversation {ConversationId}",
                            string.Join(", ", unbacked), conversationId);
                        _journal.ClaimCorrected(
                            ClaimCheck.FabricatedFigure, ClaimResolution.RePrompted,
                            ClaimNet.Review, conversationId);
                        return ReplyReview.Retry(
                            FabricatedFigureClaim.NudgeFor(unbacked), FabricatedFigureClaim.RetryNotice);
                    }

                    _logger.LogWarning(
                        "Reply still reported figure(s) {Figures} that no tool returned; correction appended. "
                        + "Conversation {ConversationId}",
                        string.Join(", ", unbacked), conversationId);
                    _journal.ClaimCorrected(
                        ClaimCheck.FabricatedFigure, ClaimResolution.Corrected,
                        ClaimNet.Review, conversationId);
                    return ReplyReview.Amend(FabricatedFigureClaim.Correction);
                }
            }

            if (scope.Staged.Count > 0 || scope.ActionPerformed)
                return ReplyReview.Accept;

            if (!UnbackedActionClaim.IsPresentIn(text))
                return ReplyReview.Accept;

            if (!rePrompted)
            {
                rePrompted = true;
                _logger.LogWarning(
                    "Reply claimed an action on a turn that has staged and run nothing; re-prompting once. "
                    + "Conversation {ConversationId}", conversationId);
                _journal.ClaimCorrected(
                    ClaimCheck.UnbackedAction, ClaimResolution.RePrompted,
                    ClaimNet.Review, conversationId);
                return ReplyReview.Retry(
                    UnbackedActionClaim.NudgeFor(userPrompt), UnbackedActionClaim.RetryNotice);
            }

            _logger.LogWarning(
                "Reply claimed an action on a turn that staged and ran nothing; correction appended. "
                + "Conversation {ConversationId}", conversationId);
            _journal.ClaimCorrected(
                ClaimCheck.UnbackedAction, ClaimResolution.Corrected,
                ClaimNet.Review, conversationId);
            return ReplyReview.Amend(UnbackedActionClaim.Correction);
        };
    }

    /// <summary>
    /// The complement of <see cref="CorrectUnbackedClaim"/>: names a staged action the reply left
    /// unmentioned. Runs ONLY on a turn that staged something, so the sentence it appends is backed by
    /// the same record the confirmation prompts come from and cannot itself be a fabricated claim.
    /// </summary>
    private string NotePendingConfirmation(
        string text, IConfirmationScope scope, string conversationId, ReplyStyle style)
    {
        if (scope.Staged.Count == 0 || scope.ActionPerformed)
            return text;

        if (PendingConfirmationNote.IsPresentIn(text))
            return text;

        _logger.LogWarning(
            "Reply left {Count} staged action(s) unmentioned; pending-confirmation note appended. "
            + "Conversation {ConversationId}", scope.Staged.Count, conversationId);

        return text + PendingConfirmationNote.For(scope.Staged.Count, style);
    }

    public async Task<ConfirmOutcome> ConfirmAsync(
        PendingConfirmation confirmation,
        bool canPerformActions,
        CancellationToken cancellationToken = default)
    {
        // Authority is checked fresh here — never trusted from the staged operation or
        // any token that carried it. Defense in depth alongside the host's own check.
        if (!canPerformActions)
            return ConfirmOutcome.Refused("You don't have permission to perform server actions.");

        return confirmation.Kind switch
        {
            ConfirmationKind.Uninstall => await ConfirmUninstallAsync(confirmation.Target, cancellationToken),
            // Install overloads ConfigKey/ConfigValue with the optional version and port overrides.
            ConfirmationKind.Install => await ConfirmInstallAsync(
                confirmation.Target, confirmation.InstanceName, cancellationToken,
                confirmation.ConfigKey, confirmation.ConfigValue, confirmation.Library),
            ConfirmationKind.SetConfig => await ConfirmSetConfigAsync(
                confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue, cancellationToken),
            ConfirmationKind.WriteFile => await ConfirmWriteFileAsync(
                confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue, cancellationToken),
            // The text path (CLI, and any surface that only reads the outcome line): finalize returns a
            // rich card, but here we surface only its summary line. A card surface (the Service) calls
            // FinalizeBlueprintAsync directly to render the DraftReady/Verified card + any re-edit token.
            // A finalize has no run-state postcondition, so its verdict is the pipeline's own.
            ConfirmationKind.Blueprint => BlueprintOutcome(await FinalizeBlueprintAsync(
                confirmation.InstanceName ?? confirmation.Target, confirmation.ConfigValue ?? string.Empty,
                canPerformActions, cancellationToken), confirmation),
            ConfirmationKind.Start or ConfirmationKind.Stop or ConfirmationKind.Restart
                or ConfirmationKind.Update or ConfirmationKind.Backup
                => await ConfirmCommandAsync(confirmation.Kind, confirmation.Target, cancellationToken),
            // These settle against no run-state postcondition — a restored backup, a kicked player and
            // a boot-autostart change leave the server's run state exactly as it was — so they take the
            // simple confirm path rather than CommandSettlement.
            ConfirmationKind.BackupRestore or ConfirmationKind.BackupDelete or ConfirmationKind.BackupPrune
                => await ConfirmBackupAsync(
                    confirmation.Kind, confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue,
                    cancellationToken),
            ConfirmationKind.PlayerKick or ConfirmationKind.PlayerBan or ConfirmationKind.PlayerUnban
                => await ConfirmPlayerAsync(
                    confirmation.Kind, confirmation.Target, confirmation.ConfigKey, cancellationToken),
            ConfirmationKind.AutostartEnable or ConfirmationKind.AutostartDisable
                => await ConfirmAutostartAsync(confirmation.Kind, confirmation.Target, cancellationToken),
            _ => ConfirmOutcome.Refused("Unknown action; nothing was done."),
        };
    }

    /// <summary>
    /// Projects a blueprint finalize onto the confirm verdict. A finalize verifies by
    /// test-installing and booting, so a <see cref="BlueprintAuthoringOutcome.Verified"/> result is
    /// its own observation; anything else is a failure the user is asked to act on.
    /// </summary>
    private static ConfirmOutcome BlueprintOutcome(
        ToolResult<BlueprintAuthoringData> result, PendingConfirmation confirmation)
    {
        var verb = ConfirmationKinds.Verb(ConfirmationKind.Blueprint);
        var target = confirmation.InstanceName ?? confirmation.Target;
        return result.Data?.Outcome == BlueprintAuthoringOutcome.Verified
            ? ConfirmOutcome.Accepted(result.Summary, verb, target)
            : ConfirmOutcome.Failed(result.Summary, verb, target);
    }

    /// <summary>
    /// Finalizes an assistant-authored blueprint after the user reviewed/edited it in the chat: re-validates
    /// the (possibly edited) YAML and runs the test-install → verify → repair → keep/stash pipeline. This is
    /// the blueprint counterpart of <see cref="ConfirmAsync"/> — it exists separately because its result is a
    /// rich <see cref="BlueprintAuthoringData"/> card (a Verified card, or a fresh DraftReady card for
    /// another edit when the repair loop exhausts), not the one-line text a normal confirm returns. Authority
    /// is checked fresh here, exactly as in <see cref="ConfirmAsync"/>.
    /// </summary>
    public async Task<ToolResult<BlueprintAuthoringData>> FinalizeBlueprintAsync(
        string game, string editedYaml, bool canPerformActions, CancellationToken cancellationToken = default)
    {
        if (!canPerformActions)
        {
            var denied = "You don't have permission to add a blueprint to the catalog.";
            return new ToolResult<BlueprintAuthoringData>(
                ResultCardKinds.BlueprintDraft, Confidence.Likely, new ResultRef(ResourceKind.Blueprint, game), denied,
                new BlueprintAuthoringData(BlueprintAuthoringOutcome.Failed, game, null, [], null, denied, false));
        }

        _logger.LogInformation("Confirmed blueprint finalize for \"{Game}\"", game);
        return await _blueprintAuthoring.FinalizeAsync(game, editedYaml, cancellationToken);
    }

    /// <summary>
    /// Executes a confirmed single-instance command (start/stop/restart/update/backup).
    /// Re-validates the target still exists (it was resolved at staging time, which may
    /// have been a while ago, and confirming is a separate, later act),
    /// then runs the matching <see cref="IServerOperations"/> op and settles the result against
    /// the observed run state — the engine's exit code says the request was accepted, which for
    /// start/stop/restart is a different claim from the server actually having got there.
    /// </summary>
    private async Task<ConfirmOutcome> ConfirmCommandAsync(
        ConfirmationKind kind, string target, CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ConfirmOutcome.Refused(
                $"'{target}' no longer exists — nothing to {ConfirmationKinds.Verb(kind)}.");

        Func<string, CancellationToken, Task<Result>> op = kind switch
        {
            ConfirmationKind.Start => _operations.StartAsync,
            ConfirmationKind.Stop => _operations.StopAsync,
            ConfirmationKind.Restart => _operations.RestartAsync,
            ConfirmationKind.Update => _operations.UpdateAsync,
            ConfirmationKind.Backup => _operations.CreateBackupAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a single-instance command"),
        };

        _logger.LogInformation("Confirmed {Verb} of {Instance}", ConfirmationKinds.Verb(kind), match);

        // _progress is the ambient sink: on a streamed confirm the settle step narrates the wait, and
        // on a buffered one Report is a silent no-op.
        var outcome = await CommandSettlement.RunAndSettleAsync(
            _operations, kind, match, op, _settlement, _progress, cancellationToken);

        if (outcome.Verdict is ConfirmVerdict.NotSettled or ConfirmVerdict.Unknown)
            _logger.LogWarning(
                "{Verb} of {Instance} did not settle: {Verdict} (observed {State})",
                ConfirmationKinds.Verb(kind), match, outcome.Verdict, outcome.ObservedState);

        return outcome;
    }

    /// <summary>
    /// Re-resolves the instance the same way every confirm path does — it was resolved when the
    /// action was staged, which may have been a while ago, and confirming is a separate, later act.
    /// Returns the live name, or null with the refusal to hand back.
    /// </summary>
    private async Task<(string? Match, ConfirmOutcome? Refusal)> ReResolveAsync(
        string target, ConfirmationKind kind, CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? (null, ConfirmOutcome.Refused(
                $"'{target}' no longer exists — nothing to {ConfirmationKinds.Verb(kind)}."))
            : (match, null);
    }

    private async Task<ConfirmOutcome> ConfirmBackupAsync(
        ConfirmationKind kind, string target, string? backupId, string? keep,
        CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(kind);
        var (match, refusal) = await ReResolveAsync(target, kind, cancellationToken);
        if (refusal is not null)
            return refusal;

        if (kind is ConfirmationKind.BackupRestore or ConfirmationKind.BackupDelete
            && string.IsNullOrWhiteSpace(backupId))
            return ConfirmOutcome.Refused("No backup was named — nothing was done.");

        _logger.LogInformation("Confirmed {Verb} of {Instance} ({Backup})", verb, match, backupId ?? keep);

        var result = kind switch
        {
            ConfirmationKind.BackupRestore =>
                await _operations.RestoreBackupAsync(match!, backupId!, cancellationToken),
            ConfirmationKind.BackupDelete =>
                await _operations.DeleteBackupAsync(match!, backupId!, cancellationToken),
            // An absent keep-count means the engine's configured retention, which it applies itself.
            _ => await _operations.PruneBackupsAsync(
                match!, int.TryParse(keep, out var n) ? n : DefaultPruneKeep, cancellationToken),
        };

        var what = kind == ConfirmationKind.BackupPrune ? "old backups" : $"backup '{backupId}'";
        return result.IsSuccess
            ? ConfirmOutcome.Accepted($"Done — {what} on '{match}' ({verb}).", verb, match!)
            : ConfirmOutcome.Failed(
                $"Could not {verb} '{match}': {result.Error ?? "unknown error"}.", verb, match!);
    }

    /// <summary>How many backups a prune keeps when the request carried no count.</summary>
    private const int DefaultPruneKeep = 5;

    private async Task<ConfirmOutcome> ConfirmPlayerAsync(
        ConfirmationKind kind, string target, string? player, CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(kind);
        var (match, refusal) = await ReResolveAsync(target, kind, cancellationToken);
        if (refusal is not null)
            return refusal;

        if (string.IsNullOrWhiteSpace(player))
            return ConfirmOutcome.Refused("No player was named — nothing was done.");

        _logger.LogInformation("Confirmed {Verb} of {Player} on {Instance}", verb, player, match);

        var result = kind switch
        {
            ConfirmationKind.PlayerKick => await _operations.KickPlayerAsync(match!, player, cancellationToken),
            ConfirmationKind.PlayerBan => await _operations.BanPlayerAsync(match!, player, cancellationToken),
            _ => await _operations.UnbanPlayerAsync(match!, player, cancellationToken),
        };

        return result.IsSuccess
            ? ConfirmOutcome.Accepted($"Done — {player} on '{match}' ({verb}).", verb, match!)
            : ConfirmOutcome.Failed(
                $"Could not {verb} '{match}': {result.Error ?? "unknown error"}.", verb, match!);
    }

    private async Task<ConfirmOutcome> ConfirmAutostartAsync(
        ConfirmationKind kind, string target, CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(kind);
        var (match, refusal) = await ReResolveAsync(target, kind, cancellationToken);
        if (refusal is not null)
            return refusal;

        var enable = kind == ConfirmationKind.AutostartEnable;
        _logger.LogInformation("Confirmed autostart {State} for {Instance}", enable ? "on" : "off", match);

        var result = await _operations.SetAutostartAsync(match!, enable, cancellationToken);
        return result.IsSuccess
            ? ConfirmOutcome.Accepted(
                enable
                    ? $"'{match}' will now start when the host boots. Its current run state is unchanged."
                    : $"'{match}' will no longer start when the host boots. Its current run state is unchanged.",
                verb, match!)
            : ConfirmOutcome.Failed(
                $"Could not {verb} '{match}': {result.Error ?? "unknown error"}.", verb, match!);
    }

    /// <summary>
    /// Re-validates the target still exists (it was resolved at staging time, which may
    /// have been a while ago, and confirming is a separate, later act),
    /// then uninstalls it.
    /// </summary>
    private async Task<ConfirmOutcome> ConfirmUninstallAsync(string target, CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(ConfirmationKind.Uninstall);
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ConfirmOutcome.Refused($"'{target}' no longer exists — nothing to uninstall.");

        _logger.LogInformation("Confirmed uninstall of {Instance}", match);

        var result = await _operations.UninstallAsync(match, cancellationToken);
        return result.IsSuccess
            ? ConfirmOutcome.Accepted($"Uninstalled '{match}'.", verb, match)
            : ConfirmOutcome.Failed(
                $"Could not uninstall '{match}': {result.Error ?? "unknown error"}.", verb, match, result.Error);
    }

    /// <summary>
    /// Re-validates the blueprint still exists and the requested name doesn't now
    /// collide (replay/race-safe), then installs.
    /// </summary>
    private async Task<ConfirmOutcome> ConfirmInstallAsync(
        string blueprint, string? instanceName, CancellationToken cancellationToken,
        string? version = null, string? port = null, string? library = null)
    {
        var verb = ConfirmationKinds.Verb(ConfirmationKind.Install);
        var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
        var match = blueprints.FirstOrDefault(
            k => string.Equals(k, blueprint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ConfirmOutcome.Refused($"Blueprint '{blueprint}' is no longer available.");

        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            if (instances.Keys.Any(k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase)))
                return ConfirmOutcome.Refused(
                    $"An instance named '{instanceName}' already exists — pick another name.");
        }
        else
        {
            instanceName = null;
        }

        _logger.LogInformation("Confirmed install of {Blueprint} (name={Name})", match, instanceName ?? "(default)");

        // A port that no longer parses is dropped rather than guessed at: the engine then uses the
        // blueprint's own port, which the outcome text reports honestly.
        var parsedPort = int.TryParse(port, out var p) && p is > 0 and <= 65535 ? p : (int?)null;
        var result = await _operations.InstallAsync(
            match, instanceName, cancellationToken,
            version: string.IsNullOrWhiteSpace(version) ? null : version,
            port: parsedPort,
            library: string.IsNullOrWhiteSpace(library) ? null : library);
        var named = instanceName is null ? "" : $" (named '{instanceName}')";
        // The library is stated only when one was chosen. Naming the engine's own default here would
        // be reporting a placement decision this path did not make.
        var placed = string.IsNullOrWhiteSpace(library) ? "" : $" in '{library}'";
        return result.IsSuccess
            ? ConfirmOutcome.Accepted($"Installed a new '{match}' server{named}{placed}.", verb, instanceName ?? match)
            : ConfirmOutcome.Failed(
                $"Could not install '{match}': {result.Error ?? "unknown error"}.", verb, match, result.Error);
    }

    /// <summary>
    /// Re-validates the instance still exists (it was resolved at staging time, and a
    /// confirming is a separate, later act), then sets one config value.
    /// kgsm owns the key-safety policy, so a refused (denylisted/invalid) key surfaces
    /// here as a failed <see cref="Result"/> reported to the user, never an exception.
    /// </summary>
    private async Task<ConfirmOutcome> ConfirmSetConfigAsync(
        string target, string? key, string? value, CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(ConfirmationKind.SetConfig);
        if (string.IsNullOrWhiteSpace(key))
            return ConfirmOutcome.Refused("No configuration key was given — nothing to set.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ConfirmOutcome.Refused($"'{target}' no longer exists — nothing to configure.");

        // The value may legitimately be the empty string (clearing the setting).
        var newValue = value ?? string.Empty;

        _logger.LogInformation("Confirmed set-config of {Instance} ({Key})", match, key);

        var result = await _operations.SetInstanceConfigValueAsync(match, key, newValue, cancellationToken);
        var shown = newValue.Length == 0 ? "(empty)" : newValue;
        return result.IsSuccess
            ? ConfirmOutcome.Accepted($"Set {key} = {shown} on '{match}'.", verb, match)
            : ConfirmOutcome.Failed(
                $"Could not set {key} on '{match}': {result.Error ?? "unknown error"}.", verb, match, result.Error);
    }

    /// <summary>
    /// Re-validates the instance still exists (it was resolved at staging time, and confirming is a
    /// separate, later act), then overwrites the file at the staged path (<paramref name="key"/>)
    /// with the staged content (<paramref name="value"/>) via <see cref="IServerOperations.WriteInstanceFileAsync"/>
    /// — the same jail the read tools use. The Service rehydrates the real content from its pending-write
    /// store BEFORE calling this (see the /confirm handler); this method always sees real content and stays
    /// store-agnostic. A jail violation, size-cap refusal, or I/O failure surfaces as a failed
    /// <see cref="Result"/>, never an exception.
    /// </summary>
    private async Task<ConfirmOutcome> ConfirmWriteFileAsync(
        string target, string? key, string? value, CancellationToken cancellationToken)
    {
        var verb = ConfirmationKinds.Verb(ConfirmationKind.WriteFile);
        if (string.IsNullOrWhiteSpace(key))
            return ConfirmOutcome.Refused("No file path was given — nothing was written.");
        if (string.IsNullOrEmpty(value))
            return ConfirmOutcome.Refused("No content was given — nothing was written.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ConfirmOutcome.Refused($"'{target}' no longer exists — nothing to write.");

        _logger.LogInformation("Confirmed write-file of {Instance} ({Path})", match, key);

        var result = await _operations.WriteInstanceFileAsync(match, key, value, cancellationToken);
        return result.IsSuccess
            ? ConfirmOutcome.Accepted(
                $"Wrote '{key}' on '{match}'. A running server picks up the change on its next restart.", verb, match)
            : ConfirmOutcome.Failed(
                $"Could not write '{key}' on '{match}': {result.Error ?? "unknown error"}.", verb, match, result.Error);
    }

    /// <summary>
    /// A search query reduced to its content words, so a repeat survives cosmetic variation: case,
    /// punctuation, filler words and word order all move between a model's retries while the question
    /// does not. "Difficulty option values" and "values, Difficulty option?" are one search.
    /// <para>
    /// This catches a REPEAT, not a near-miss: "Difficulty option values" and "Difficulty options" stay
    /// distinct, so a model rewording its way around the same question is not fully stopped. That is
    /// deliberate — the cost of the two errors is not symmetric. A missed duplicate wastes one search
    /// from a budget of five; a false match refuses a question the model has not actually asked yet,
    /// and no similarity threshold distinguishes those reliably enough to risk it.
    /// </para>
    /// </summary>
    private static string Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var words = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9.]+")
            .Select(m => m.Value)
            .Where(w => w is not ("the" or "a" or "an" or "of" or "for" or "to" or "in" or "on" or "and" or "or"))
            .Distinct()
            .Order(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Per-turn gate closure.
    ///  - Read-only tools always pass.
    ///  - Authorized reads (read_file / list_files): refused for unauthorized callers, but not capped.
    ///  - Commands (start/stop/restart/update/backup/install/uninstall): refused for unauthorized
    ///    callers; otherwise allowed through to the dispatcher, which only STAGES them (it never
    ///    executes). Every command is propose-only, so there is one cap — the count of ops
    ///    proposed this message — at <see cref="MaxStagedCommandsPerMessage"/>.
    /// The closure holds the per-message staging counter.
    /// </summary>
    /// <summary>
    /// The per-call authorization and blast-radius gate for one message.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Only the authorization refusals are recorded.</b> The caps below — staged commands,
    /// searches, fetches, a repeated lookup — are loop guards firing on ordinary model over-eagerness,
    /// and journalling them would bury the ones that mean somebody reached past their tier under a
    /// stream of the model being enthusiastic.
    /// </remarks>
    /// <summary>
    /// Refuses a call for want of authority, and records that somebody reached for it.
    /// </summary>
    /// <remarks>
    /// One helper so the record and the refusal cannot come apart: a refusal added later without its
    /// line would be a permission check that leaves no trace, which is the gap this closes.
    /// </remarks>
    private ToolGate Declined(LlmToolCall call, string message)
    {
        _journal.ActionDeclined(call.Name.Name, call.Arg("instance_name")?.Trim());
        return ToolGate.Refuse(message);
    }

    private Func<LlmToolCall, ToolGate> BuildGate(bool canPerformActions)
    {
        var staged = 0;
        var searches = 0;
        var fetches = 0;
        var authored = 0;
        var remembered = 0;
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return call =>
        {
            // The gate authorizes a CAPABILITY, resolved from whatever the file calls it this deploy.
            // A name nothing implements is refused here rather than reaching a handler that would have
            // to decide what an unknown tool means.
            var capability = _toolCatalog.CapabilityOf(call.Name);

            // The personal tier (LlmTools.PersonalTier) needs no server authority — it reaches the
            // caller's own memory and nothing else, so canPerformActions is deliberately not consulted.
            // What bounds it is a per-message cap on the WRITES: one turn settling something is normal,
            // a turn writing a dozen memories is a runaway filling the person's whole store.
            if (capability is { } personal && LlmTools.PersonalTier.Contains(personal))
            {
                if (personal != LlmTools.Remember)
                    return ToolGate.Allow;

                if (remembered >= MaxMemoryWritesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxMemoryWritesPerMessage} thing(s) can be written down per " +
                        "message. Say what else is worth remembering and it can be recorded next turn.");

                remembered++;
                return ToolGate.Allow;
            }

            // create_blueprint is authorized-and-autonomous (LlmTools.AuthorizedActions): refused for an
            // unauthorized caller exactly like a staged command (defense in depth behind SelectTools
            // already omitting it for those callers), but it never stages — it runs to completion inline,
            // so its own per-message cap replaces the staging cap below.
            if (capability == LlmTools.CreateBlueprint)
            {
                if (!canPerformActions)
                    return Declined(call, "Refused: you don't have permission to perform server actions.");

                if (authored >= MaxBlueprintAuthoringsPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxBlueprintAuthoringsPerMessage} new blueprint(s) can be " +
                        "authored per message. Ask the user to try the rest separately.");

                authored++;
                return ToolGate.Allow;
            }

            // revise_blueprint: authorized + inline like create_blueprint (SelectTools only offers it on a
            // draft-bearing turn). Refuse an unauthorized caller as defense in depth; no per-message cap of
            // its own — a user may refine a draft several times, and the loop-iteration guard bounds runaway.
            if (capability == LlmTools.ReviseBlueprint)
                return canPerformActions
                    ? ToolGate.Allow
                    : ToolGate.Refuse("Refused: you don't have permission to perform server actions.");


            // search is read-only (open to everyone), but each call adds a loop iteration (and a web
            // fallback may spend a credit), so it carries its own per-message cap — the in-turn
            // runaway guard (the per-day web wallet ceiling lives host-side). Checked before the
            // read-only pass-through below.
            if (capability == LlmTools.Search)
            {
                // A query already run this message cannot return anything new, so re-running it only
                // spends the budget and the turn's iterations on an answer already in context. Refused
                // WITHOUT counting against the cap: the call was free of information, so charging for
                // it would punish the model twice for one mistake and leave less room to recover.
                // ⚠ Keyed on the query AND where it looked. The same words asked of the local
                // documentation and of the web are two different questions with two different answers,
                // and refusing the second as a repeat is what made "it wasn't in the docs, try online"
                // impossible — the model asked, was told it had already searched, and gave up. The
                // refusal even said so in as many words, which was simply untrue across sources.
                var query = Normalize(call.Arg("query"));
                var where = Normalize(call.Arg("scope"));
                if (query.Length > 0 && !searched.Add($"{where} {query}"))
                    return ToolGate.Refuse(
                        "Refused: you already searched for that, in that same place, this message. " +
                        "Searching the SAME place for the same words returns the same thing — but if " +
                        "the local documentation didn't answer it, searching again with scope=\"web\" " +
                        "is a different question and is allowed. Otherwise use what you have, ask the " +
                        "user, or say you couldn't find it.");

                if (searches >= MaxSearchesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxSearchesPerMessage} searches per message. " +
                        "Answer from what you already found, or tell the user you couldn't find it.");

                searches++;
                return ToolGate.Allow;
            }

            // fetch_url is likewise read-only and open to everyone, but each call is a real outbound
            // HTTP request against a model/user-influenced URL and adds a loop iteration — its own
            // per-message cap, independent of the search cap.
            if (capability == LlmTools.FetchUrl)
            {
                if (fetches >= MaxFetchesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxFetchesPerMessage} page fetches per message. " +
                        "Answer from what you already fetched, or tell the user you couldn't fetch it.");

                fetches++;
                return ToolGate.Allow;
            }

            if (capability is not null && LlmTools.IsAuthorizedRead(capability.Value))
                return canPerformActions
                    ? ToolGate.Allow
                    : Declined(call, "Refused: you don't have permission to read server files.");

            if (capability is null || !LlmTools.IsStagedCommand(capability.Value))
                return ToolGate.Allow; // read-only

            if (!canPerformActions)
                return Declined(call, "Refused: you don't have permission to perform server actions.");

            if (staged >= MaxStagedCommandsPerMessage)
                return ToolGate.Refuse(
                    $"Refused: at most {MaxStagedCommandsPerMessage} server actions can be proposed " +
                    "per message. Ask the user to do the rest separately.");

            staged++;
            return ToolGate.Allow;
        };
    }
}
