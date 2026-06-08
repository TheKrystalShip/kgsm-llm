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
}
