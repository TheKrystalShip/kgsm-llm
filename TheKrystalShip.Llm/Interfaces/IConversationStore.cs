using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Stores short-term conversation history per conversation id so the LLM can
/// follow multi-turn context (e.g. "the pvp one" referring to a prior question).
/// Implementations roll the window to a fixed size and reset after idle time.
/// The system prompt is NOT stored here — it is rebuilt fresh each turn.
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Returns the current history for a conversation, oldest first. Returns an
    /// empty list if there is no history or the conversation has gone idle.
    /// </summary>
    IReadOnlyList<LlmMessage> GetHistory(string conversationId);

    /// <summary>
    /// Appends one or more messages to a conversation, resetting it first if it
    /// has been idle, then trimming to the configured window size.
    /// </summary>
    void Append(string conversationId, params LlmMessage[] messages);
}
