using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Abstraction over a local LLM backend (e.g. Ollama). Supports plain text
/// completion and tool calling.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a conversation to the model and returns its reply, which may be
    /// text, tool calls, or both.
    /// </summary>
    /// <param name="messages">Ordered conversation history (system first).</param>
    /// <param name="tools">
    /// Tools the model is allowed to call this turn. This set is the whitelist;
    /// pass null/empty for a plain text completion.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<LlmResponse>> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming counterpart to <see cref="ChatAsync"/>: sends the conversation and yields the
    /// model's reply as a sequence of <see cref="LlmStreamChunk"/> frames (content deltas, tool
    /// calls, and a terminal done marker) as they arrive. Transport/backend failures surface as a
    /// thrown exception when the consumer advances the enumerator (the agent loop maps it to a
    /// terminal error event); disposing the enumerator early aborts the underlying request.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
