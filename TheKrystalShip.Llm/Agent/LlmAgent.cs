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

    public async Task<Result<string>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        // Record the user's turn, then assemble [fresh system, ...history].
        _conversationStore.Append(turn.ConversationId, LlmMessage.User(turn.UserPrompt));

        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetHistory(turn.ConversationId));

        var tools = turn.Tools;
        var gate = turn.Gate;

        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            var response = await _llmClient.ChatAsync(working, tools, cancellationToken);
            if (response.IsFailure)
                return Result.Failure<string>(response.Error!);

            var message = response.Value!;

            if (!message.HasToolCalls)
            {
                var text = message.Content ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    _conversationStore.Append(turn.ConversationId, LlmMessage.Assistant(text));
                return Result.Success(text);
            }

            await ExecuteToolRoundAsync(message.ToolCalls, working, gate, cancellationToken);
        }

        _logger.LogWarning(
            "Agent hit the {Max}-iteration cap for conversation {Conversation}",
            _options.MaxIterations, turn.ConversationId);
        return Result.Success(_options.IterationLimitReply);
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamAsync(
        AgentTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Identical persistence boundary to RunAsync: record the user's turn, then assemble
        // [fresh system, ...history]. Only the final no-tool-call text is persisted below.
        _conversationStore.Append(turn.ConversationId, LlmMessage.User(turn.UserPrompt));

        var working = new List<LlmMessage> { LlmMessage.System(turn.SystemPrompt) };
        working.AddRange(_conversationStore.GetHistory(turn.ConversationId));

        var tools = turn.Tools;
        var gate = turn.Gate;

        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            var content = new StringBuilder();
            List<LlmToolCall>? toolCalls = null;
            string? error = null;

            // Drive the chunk stream through a manual enumerator: a mid-stream failure must be
            // captured and surfaced as a terminal error event, and C# forbids `yield` in a catch.
            await using var chunks = _llmClient
                .ChatStreamAsync(working, tools, cancellationToken)
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

                // Tool calls arrive complete in one frame (probe-verified) — capture, don't accumulate.
                if (chunk.ToolCalls is { Count: > 0 })
                    toolCalls = chunk.ToolCalls.ToList();

                if (chunk.Done)
                    break;
            }

            if (error is not null)
            {
                yield return AgentEvent.Error(error);
                yield break;
            }

            if (toolCalls is { Count: > 0 })
            {
                // A tool round emits no user-facing prose; intermediate content is NOT persisted
                // (parity with RunAsync, which only ever stores the final no-tool-call text).
                await ExecuteToolRoundAsync(toolCalls, working, gate, cancellationToken);
                yield return AgentEvent.Status(DescribeToolRound(toolCalls));
                continue;
            }

            // Final turn: persist only the final assistant text (trimmed, matching the buffered
            // client which trims the whole reply), then signal completion.
            var text = content.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                _conversationStore.Append(turn.ConversationId, LlmMessage.Assistant(text));
            yield return AgentEvent.Final(text);
            yield break;
        }

        _logger.LogWarning(
            "Agent hit the {Max}-iteration cap (stream) for conversation {Conversation}",
            _options.MaxIterations, turn.ConversationId);
        yield return AgentEvent.Final(_options.IterationLimitReply);
    }

    /// <summary>
    /// Runs one tool round: append the assistant's tool-call turn, gate-then-dispatch each call
    /// concurrently (a refused call feeds its refusal back instead of executing), then append one
    /// tool-result message per call, in order. Shared by <see cref="RunAsync"/> and
    /// <see cref="RunStreamAsync"/> so the gate/dispatch/truncate semantics can't drift.
    /// </summary>
    private async Task ExecuteToolRoundAsync(
        IReadOnlyList<LlmToolCall> toolCalls,
        List<LlmMessage> working,
        Func<LlmToolCall, ToolGate>? gate,
        CancellationToken cancellationToken)
    {
        working.Add(LlmMessage.AssistantToolCalls(toolCalls));

        var outputs = await Task.WhenAll(toolCalls.Select(async call =>
        {
            var decision = gate?.Invoke(call) ?? ToolGate.Allow;
            if (!decision.Allowed)
            {
                return decision.RefusalMessage
                    ?? $"Refused: the '{call.Name}' tool is not permitted right now.";
            }

            return await _dispatcher.ExecuteAsync(call, cancellationToken);
        }));

        for (int i = 0; i < toolCalls.Count; i++)
            working.Add(LlmMessage.Tool(toolCalls[i].Name, Truncate(outputs[i], _options.MaxToolOutputChars)));
    }

    private static string DescribeToolRound(IReadOnlyList<LlmToolCall> toolCalls)
    {
        var names = toolCalls.Select(c => c.Name).Distinct().ToArray();
        return $"Running {string.Join(", ", names)}…";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";
}
