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

            // Append the assistant's tool-call turn, then one tool-result per call, in order.
            working.Add(LlmMessage.AssistantToolCalls(message.ToolCalls));
            var outputs = await Task.WhenAll(message.ToolCalls.Select(async call =>
            {
                var decision = gate?.Invoke(call) ?? ToolGate.Allow;
                if (!decision.Allowed)
                {
                    return decision.RefusalMessage
                        ?? $"Refused: the '{call.Name}' tool is not permitted right now.";
                }

                return await _dispatcher.ExecuteAsync(call, cancellationToken);
            }));

            var calls = message.ToolCalls.ToList();
            for (int i = 0; i < calls.Count; i++)
            {
                working.Add(LlmMessage.Tool(calls[i].Name, Truncate(outputs[i], _options.MaxToolOutputChars)));
            }
        }

        _logger.LogWarning(
            "Agent hit the {Max}-iteration cap for conversation {Conversation}",
            _options.MaxIterations, turn.ConversationId);
        return Result.Success(_options.IterationLimitReply);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";
}
