using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Stores conversation history per conversation id so the LLM can follow multi-turn context
/// (e.g. "the pvp one" referring to a prior question). Implementations roll the window to a fixed size.
/// The conversation id is the canonical scope (a fresh chat is a fresh id) — there is no idle reset; a
/// conversation is retained and resumable by id. The system prompt is NOT stored here — it is rebuilt
/// fresh each turn.
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Returns the current history for a conversation, oldest first. Returns an
    /// empty list if there is no history for the id.
    /// </summary>
    IReadOnlyList<LlmMessage> GetHistory(string conversationId);

    /// <summary>
    /// Appends one or more messages to a conversation, then trims to the configured window size.
    /// </summary>
    void Append(string conversationId, params LlmMessage[] messages);

    /// <summary>
    /// Atomically replaces a conversation's entire history with the given messages, trimmed to the
    /// configured window size. This is the seam compaction uses to swap a full history for a single
    /// summary message; passing no messages clears the conversation.
    /// </summary>
    void Replace(string conversationId, params LlmMessage[] messages);
}
