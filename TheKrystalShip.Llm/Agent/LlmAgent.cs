using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Agent;

/// <summary>
/// Drives the model↔tool agent loop. Application-agnostic: the host supplies the
/// tools, system prompt, and per-call gate via <see cref="AgentTurn"/>; this loop
/// only knows how to call the model, gate-then-dispatch tool calls, feed results
/// back, and persist the conversation.
///
/// Persistence: each completed turn is appended to the canonical <see cref="IConversationStore"/> as
/// one rich <see cref="ConversationTurnRecord"/> — the user prompt, the model's thinking, this turn's
/// full tool trajectory, the final reply, token usage and outcome. That single append-only log is BOTH
/// the model's continuity memory and the durable record mined for self-improvement. The model only
/// replays a projection of it (<see cref="IConversationStore.GetModelContext"/>): the user prompt +
/// final reply of prior turns (plus the latest checkpoint summary). The intermediate assistant-tool-call
/// and tool-result messages live in the per-turn working list only — they are recorded in the turn's
/// trajectory for analysis but never replayed, so follow-ups re-query live data fresh.
/// </summary>
public class LlmAgent : ILlmAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IToolDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly LlmAgentOptions _options;
    private readonly ILogger<LlmAgent> _logger;

    public LlmAgent(
        ILlmClient llmClient,
        IToolDispatcher dispatcher,
        IConversationStore conversationStore,
        IOptions<LlmAgentOptions> options,
        ILogger<LlmAgent> logger)
    {
        _llmClient = llmClient;
        _dispatcher = dispatcher;
        _conversationStore = conversationStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<AgentRunResult>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var trajectory = new List<RecordedToolCall>();
        var iterationsRun = 0;

        // Assemble [fresh system, ...projected history, this turn's user prompt]. The user prompt is
        // not yet in the store — it is persisted with the whole turn at completion (PersistTurn).
        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetModelContext(turn.ConversationId));
        working.Add(LlmMessage.User(turn.UserPrompt));

        var tools = turn.Tools;
        var gate = turn.Gate;

        try
        {
            for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
            {
                iterationsRun = iteration + 1;

                var response = await _llmClient.ChatAsync(working, tools, turn.Think, cancellationToken);
                if (response.IsFailure)
                {
                    PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, response.Error, null);
                    return Result.Failure<AgentRunResult>(response.Error!);
                }

                var message = response.Value!;

                if (!message.HasToolCalls)
                {
                    var text = message.Content ?? string.Empty;
                    // Usage of the producing (final) call — the turn's context occupancy.
                    PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Ok, text, message.Usage, null, null);
                    return Result.Success(new AgentRunResult(text, message.Usage));
                }

                await ExecuteToolRoundAsync(message.ToolCalls, working, gate, trajectory, cancellationToken);
            }

            _logger.LogWarning(
                "Agent hit the {Max}-iteration cap for conversation {Conversation}",
                _options.MaxIterations, turn.ConversationId);
            PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.CapHit, _options.IterationLimitReply, null, null, null);
            return Result.Success(new AgentRunResult(_options.IterationLimitReply, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user/host abandoned the turn mid-flight — analytically interesting, so capture it.
            PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Cancelled, null, null, null, null);
            throw;
        }
        catch (Exception ex)
        {
            // An unexpected throw (a dispatcher that doesn't swallow, or a non-token cancellation) is
            // an errored turn, not a user bail-out — record it as such, then propagate unchanged.
            PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, ex.Message, null);
            throw;
        }
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamAsync(
        AgentTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var trajectory = new List<RecordedToolCall>();
        var iterationsRun = 0;
        // The model's reasoning across the WHOLE turn (every round), captured for the record only —
        // never replayed into the model's context.
        var thinking = new StringBuilder();
        // A terminal record was emitted (Ok/Error/CapHit). The finally then captures only the case
        // we can't reach in-body: cancellation / an exception unwinding the iterator before a finish.
        var recorded = false;

        // Assemble [fresh system, ...projected history, this turn's user prompt]. Identical boundary to
        // RunAsync; the whole turn (incl. the prompt) is persisted at completion via PersistTurn.
        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetModelContext(turn.ConversationId));
        working.Add(LlmMessage.User(turn.UserPrompt));

        var tools = turn.Tools;
        var gate = turn.Gate;
        // Monotonic across the whole turn (every round) so each tool call gets a unique, stable id
        // that pairs its tool.start with its tool.result. Ollama supplies no native tool-call id.
        var toolCallSeq = 0;

        try
        {
            for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
            {
                iterationsRun = iteration + 1;

                var content = new StringBuilder();
                List<LlmToolCall>? toolCalls = null;
                string? error = null;
                LlmUsage? usage = null;

                // Drive the chunk stream through a manual enumerator: a mid-stream failure must be
                // captured and surfaced as a terminal error event, and C# forbids `yield` in a catch.
                await using var chunks = _llmClient
                    .ChatStreamAsync(working, tools, turn.Think, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                while (true)
                {
                    LlmStreamChunk? chunk = null;
                    try
                    {
                        if (await chunks.MoveNextAsync())
                            chunk = chunks.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // genuine cancellation (e.g. the client disconnected) — let it propagate
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    if (error is not null || chunk is null)
                        break;

                    if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    {
                        content.Append(chunk.ContentDelta);
                        yield return AgentEvent.Token(chunk.ContentDelta);
                    }

                    if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
                    {
                        thinking.Append(chunk.ThinkingDelta);
                        yield return AgentEvent.Thinking(chunk.ThinkingDelta);
                    }

                    // Tool calls arrive complete in one frame (probe-verified) — capture, don't accumulate.
                    if (chunk.ToolCalls is { Count: > 0 })
                        toolCalls = chunk.ToolCalls.ToList();

                    // Token counts ride the terminal done frame of this generation.
                    if (chunk.Usage is not null)
                        usage = chunk.Usage;

                    if (chunk.Done)
                        break;
                }

                if (error is not null)
                {
                    PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, error, thinking.ToString());
                    recorded = true;
                    yield return AgentEvent.Error(error);
                    yield break;
                }

                if (toolCalls is { Count: > 0 })
                {
                    // A tool round emits no user-facing prose; intermediate content is NOT replayed
                    // (parity with RunAsync). Surface per-tool events: all tool.start (input order)
                    // BEFORE dispatch, then all tool.result (input order) after — a deterministic,
                    // batched order that matches the model-feedback append order below. (Dispatch/gate/
                    // truncate stay centralised in the shared helpers so the paths can't drift.)
                    working.Add(LlmMessage.AssistantToolCalls(toolCalls));
                    // Mint the per-call ids once, up front, so the matching tool.start/tool.result
                    // (emitted in separate loops, same index order) carry the SAME id.
                    var callIds = new string[toolCalls.Count];
                    for (int i = 0; i < toolCalls.Count; i++)
                        callIds[i] = $"tc_{toolCallSeq++}";
                    for (int i = 0; i < toolCalls.Count; i++)
                        yield return AgentEvent.ToolStart(toolCalls[i].Name, toolCalls[i].Arguments, callIds[i]);

                    var outputs = await DispatchRoundAsync(toolCalls, gate, cancellationToken);

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        working.Add(LlmMessage.Tool(toolCalls[i].Name, Truncate(outputs[i].Output, _options.MaxToolOutputChars)));
                        // The model only sees the string Output (above); the structured Data card is NOT
                        // replayed to the model, but IS persisted in the trajectory so a surface can
                        // re-scaffold the §5·a tool.result (summary + card) from history.
                        trajectory.Add(new RecordedToolCall(
                            toolCalls[i].Name, toolCalls[i].Arguments, outputs[i].Output, outputs[i].DurationMs, outputs[i].Data));
                        // Data also rides the live event opaquely to the streaming surface.
                        yield return AgentEvent.ToolResult(toolCalls[i].Name, outputs[i].Output, callIds[i], outputs[i].Data);
                    }
                    continue;
                }

                // Final turn: persist the whole turn (the canonical record), then signal completion.
                var text = content.ToString().Trim();
                PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Ok, text, usage, null, thinking.ToString());
                recorded = true;
                yield return AgentEvent.Final(text, usage);
                yield break;
            }

            _logger.LogWarning(
                "Agent hit the {Max}-iteration cap (stream) for conversation {Conversation}",
                _options.MaxIterations, turn.ConversationId);
            PersistTurn(turn, startedAt, trajectory, iterationsRun, TurnOutcome.CapHit, _options.IterationLimitReply, null, null, thinking.ToString());
            recorded = true;
            yield return AgentEvent.Final(_options.IterationLimitReply);
        }
        finally
        {
            // Reached on normal completion (recorded already true → skip) AND on any exception
            // unwinding the iterator before a terminal record — including a consumer that stops
            // enumerating after Ctrl-C (disposal runs this finally). A genuine cancellation has the
            // token requested by now → Cancelled; an unexpected throw (e.g. a dispatcher that doesn't
            // swallow) records Error instead of masquerading as a user bail-out. The buffered path
            // discriminates the same way via its catch filter.
            if (!recorded)
                PersistTurn(turn, startedAt, trajectory, iterationsRun,
                    cancellationToken.IsCancellationRequested ? TurnOutcome.Cancelled : TurnOutcome.Error,
                    null, null, null, thinking.ToString());
        }
    }

    /// <summary>
    /// Runs one tool round for the buffered path: append the assistant's tool-call turn,
    /// gate-then-dispatch, then append one tool-result message per call, in order, recording each into
    /// <paramref name="trajectory"/>. Delegates the gate/dispatch to <see cref="DispatchRoundAsync"/>
    /// (shared with the streaming path).
    /// </summary>
    private async Task ExecuteToolRoundAsync(
        IReadOnlyList<LlmToolCall> toolCalls,
        List<LlmMessage> working,
        Func<LlmToolCall, ToolGate>? gate,
        List<RecordedToolCall> trajectory,
        CancellationToken cancellationToken)
    {
        working.Add(LlmMessage.AssistantToolCalls(toolCalls));
        var outputs = await DispatchRoundAsync(toolCalls, gate, cancellationToken);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            working.Add(LlmMessage.Tool(toolCalls[i].Name, Truncate(outputs[i].Output, _options.MaxToolOutputChars)));
            trajectory.Add(new RecordedToolCall(
                toolCalls[i].Name, toolCalls[i].Arguments, outputs[i].Output, outputs[i].DurationMs, outputs[i].Data));
        }
    }

    /// <summary>
    /// Gate-then-dispatch one tool round concurrently (a refused call yields its refusal string
    /// instead of executing) and return one timed output per call, in input order. The single source
    /// of gate/dispatch semantics for both <see cref="RunAsync"/> and <see cref="RunStreamAsync"/>.
    /// </summary>
    private async Task<ToolExecution[]> DispatchRoundAsync(
        IReadOnlyList<LlmToolCall> toolCalls,
        Func<LlmToolCall, ToolGate>? gate,
        CancellationToken cancellationToken)
    {
        return await Task.WhenAll(toolCalls.Select(async call =>
        {
            var decision = gate?.Invoke(call) ?? ToolGate.Allow;
            if (!decision.Allowed)
            {
                return new ToolExecution(
                    decision.RefusalMessage
                        ?? $"Refused: the '{call.Name}' tool is not permitted right now.",
                    0);
            }

            var started = Stopwatch.GetTimestamp();
            var output = await _dispatcher.ExecuteAsync(call, cancellationToken);
            return new ToolExecution(
                output.Summary, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, output.Data);
        }));
    }

    /// <summary>
    /// Appends one completed turn to the canonical conversation store, failure-isolated: a store write
    /// error is logged, never propagated — a turn must not fail over its own bookkeeping.
    /// </summary>
    private void PersistTurn(
        AgentTurn turn, DateTimeOffset startedAt, IReadOnlyList<RecordedToolCall> trajectory,
        int iterations, TurnOutcome outcome, string? final, LlmUsage? usage, string? error, string? thinking)
    {
        try
        {
            _conversationStore.AppendTurn(new ConversationTurnRecord
            {
                ConversationId = turn.ConversationId,
                UserDisplay = turn.UserDisplay,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                UserPrompt = turn.UserPrompt,
                // Prefer the host's template fingerprint (tracks prompt EDITS, not injected lists);
                // fall back to hashing the whole assembled prompt for hosts that don't supply one.
                SystemPromptHash = turn.SystemPromptHash ?? PromptHash.Short(turn.SystemPrompt),
                Tools = trajectory,
                Iterations = iterations,
                Outcome = outcome,
                Think = turn.Think,
                Thinking = string.IsNullOrEmpty(thinking) ? null : thinking,
                Final = final,
                Usage = usage,
                Error = error,
            });
        }
        catch (Exception ex)
        {
            // Defence in depth: never fail a turn over persisting its record.
            _logger.LogWarning(ex, "Failed to persist conversation turn for {Conversation}", turn.ConversationId);
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";

    /// <summary>One tool call's raw output and how long the dispatch took.</summary>
    // Output = the model-facing summary; Data = the dispatcher's optional opaque surface card
    // (ToolOutput.Data), index-aligned to its call by Task.WhenAll's input-order guarantee.
    private readonly record struct ToolExecution(string Output, long DurationMs, object? Data = null);
}
