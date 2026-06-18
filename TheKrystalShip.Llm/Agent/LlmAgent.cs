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
/// Persistence boundary: only the user's text and the final assistant text are
/// written to the conversation store. The intermediate assistant-tool-call and
/// tool-result messages live in the per-turn working list only — persisting them
/// would risk the store's count-based trim splitting a tool-call/result pair,
/// and keeps live data (e.g. uptime) out of history so follow-ups re-query fresh.
///
/// Recording boundary (separate concern): when an <see cref="IConversationRecorder"/>
/// is enabled, the loop ALSO emits one append-only <see cref="ConversationTurnRecord"/>
/// per turn — the full tool trajectory, iteration count, usage and outcome — for offline
/// self-improvement analysis. That is distinct from the lossy working memory above; the
/// recorder never trims, resets, or overwrites.
/// </summary>
public class LlmAgent : ILlmAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IToolDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationRecorder _recorder;
    private readonly LlmAgentOptions _options;
    private readonly ILogger<LlmAgent> _logger;

    public LlmAgent(
        ILlmClient llmClient,
        IToolDispatcher dispatcher,
        IConversationStore conversationStore,
        IConversationRecorder recorder,
        IOptions<LlmAgentOptions> options,
        ILogger<LlmAgent> logger)
    {
        _llmClient = llmClient;
        _dispatcher = dispatcher;
        _conversationStore = conversationStore;
        _recorder = recorder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<AgentRunResult>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        // Recording is a side-channel: only build the trajectory when a recorder is listening.
        var startedAt = DateTimeOffset.UtcNow;
        var trajectory = _recorder.Enabled ? new List<RecordedToolCall>() : null;
        var iterationsRun = 0;

        // Record the user's turn, then assemble [fresh system, ...history].
        _conversationStore.Append(turn.ConversationId, LlmMessage.User(turn.UserPrompt));

        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetHistory(turn.ConversationId));

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
                    Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, response.Error);
                    return Result.Failure<AgentRunResult>(response.Error!);
                }

                var message = response.Value!;

                if (!message.HasToolCalls)
                {
                    var text = message.Content ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                        _conversationStore.Append(turn.ConversationId, LlmMessage.Assistant(text));
                    // Usage of the producing (final) call — the turn's context occupancy.
                    Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Ok, text, message.Usage, null);
                    return Result.Success(new AgentRunResult(text, message.Usage));
                }

                await ExecuteToolRoundAsync(message.ToolCalls, working, gate, trajectory, cancellationToken);
            }

            _logger.LogWarning(
                "Agent hit the {Max}-iteration cap for conversation {Conversation}",
                _options.MaxIterations, turn.ConversationId);
            Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.CapHit, _options.IterationLimitReply, null, null);
            return Result.Success(new AgentRunResult(_options.IterationLimitReply, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user/host abandoned the turn mid-flight — analytically interesting, so capture it.
            Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Cancelled, null, null, null);
            throw;
        }
        catch (Exception ex)
        {
            // An unexpected throw (a dispatcher that doesn't swallow, or a non-token cancellation) is
            // an errored turn, not a user bail-out — record it as such, then propagate unchanged.
            Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamAsync(
        AgentTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var trajectory = _recorder.Enabled ? new List<RecordedToolCall>() : null;
        var iterationsRun = 0;
        // A terminal record was emitted (Ok/Error/CapHit). The finally then captures only the case
        // we can't reach in-body: cancellation / an exception unwinding the iterator before a finish.
        var recorded = false;

        // Identical persistence boundary to RunAsync: record the user's turn, then assemble
        // [fresh system, ...history]. Only the final no-tool-call text is persisted below.
        _conversationStore.Append(turn.ConversationId, LlmMessage.User(turn.UserPrompt));

        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetHistory(turn.ConversationId));

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
                        yield return AgentEvent.Thinking(chunk.ThinkingDelta);

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
                    Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Error, null, null, error);
                    recorded = true;
                    yield return AgentEvent.Error(error);
                    yield break;
                }

                if (toolCalls is { Count: > 0 })
                {
                    // A tool round emits no user-facing prose; intermediate content is NOT persisted
                    // (parity with RunAsync, which only ever stores the final no-tool-call text).
                    // Surface per-tool events: all tool.start (input order) BEFORE dispatch, then all
                    // tool.result (input order) after — a deterministic, batched order that matches the
                    // model-feedback append order below. (Dispatch/gate/truncate stay centralised in the
                    // shared helpers so the buffered and streaming paths can't drift.)
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
                        trajectory?.Add(new RecordedToolCall(
                            toolCalls[i].Name, toolCalls[i].Arguments, outputs[i].Output, outputs[i].DurationMs));
                        yield return AgentEvent.ToolResult(toolCalls[i].Name, outputs[i].Output, callIds[i]);
                    }
                    continue;
                }

                // Final turn: persist only the final assistant text (trimmed, matching the buffered
                // client which trims the whole reply), then signal completion.
                var text = content.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    _conversationStore.Append(turn.ConversationId, LlmMessage.Assistant(text));
                Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.Ok, text, usage, null);
                recorded = true;
                yield return AgentEvent.Final(text, usage);
                yield break;
            }

            _logger.LogWarning(
                "Agent hit the {Max}-iteration cap (stream) for conversation {Conversation}",
                _options.MaxIterations, turn.ConversationId);
            Record(turn, startedAt, trajectory, iterationsRun, TurnOutcome.CapHit, _options.IterationLimitReply, null, null);
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
            if (_recorder.Enabled && !recorded)
                Record(turn, startedAt, trajectory, iterationsRun,
                    cancellationToken.IsCancellationRequested ? TurnOutcome.Cancelled : TurnOutcome.Error,
                    null, null, null);
        }
    }

    /// <summary>
    /// Runs one tool round for the buffered path: append the assistant's tool-call turn,
    /// gate-then-dispatch, then append one tool-result message per call, in order, recording each
    /// into <paramref name="trajectory"/> when capturing. Delegates the gate/dispatch to
    /// <see cref="DispatchRoundAsync"/> (shared with the streaming path).
    /// </summary>
    private async Task ExecuteToolRoundAsync(
        IReadOnlyList<LlmToolCall> toolCalls,
        List<LlmMessage> working,
        Func<LlmToolCall, ToolGate>? gate,
        List<RecordedToolCall>? trajectory,
        CancellationToken cancellationToken)
    {
        working.Add(LlmMessage.AssistantToolCalls(toolCalls));
        var outputs = await DispatchRoundAsync(toolCalls, gate, cancellationToken);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            working.Add(LlmMessage.Tool(toolCalls[i].Name, Truncate(outputs[i].Output, _options.MaxToolOutputChars)));
            trajectory?.Add(new RecordedToolCall(
                toolCalls[i].Name, toolCalls[i].Arguments, outputs[i].Output, outputs[i].DurationMs));
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
            return new ToolExecution(output, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }));
    }

    /// <summary>Builds and emits one turn record, failure-isolated. No-op when recording is off.</summary>
    private void Record(
        AgentTurn turn, DateTimeOffset startedAt, IReadOnlyList<RecordedToolCall>? trajectory,
        int iterations, TurnOutcome outcome, string? final, LlmUsage? usage, string? error)
    {
        if (!_recorder.Enabled)
            return;

        try
        {
            _recorder.Record(new ConversationTurnRecord
            {
                ConversationId = turn.ConversationId,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                UserPrompt = turn.UserPrompt,
                // Prefer the host's template fingerprint (tracks prompt EDITS, not injected lists);
                // fall back to hashing the whole assembled prompt for hosts that don't supply one.
                SystemPromptHash = turn.SystemPromptHash ?? PromptHash.Short(turn.SystemPrompt),
                Tools = trajectory ?? Array.Empty<RecordedToolCall>(),
                Iterations = iterations,
                Outcome = outcome,
                Final = final,
                Usage = usage,
                Error = error,
            });
        }
        catch (Exception ex)
        {
            // Defence in depth on top of the recorder's own guard: never fail a turn over its record.
            _logger.LogWarning(ex, "Failed to build conversation turn record for {Conversation}", turn.ConversationId);
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";

    /// <summary>One tool call's raw output and how long the dispatch took.</summary>
    private readonly record struct ToolExecution(string Output, long DurationMs);
}
