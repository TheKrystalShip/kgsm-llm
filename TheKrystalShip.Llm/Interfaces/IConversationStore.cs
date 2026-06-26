using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// The canonical, append-only conversation history: an ordered log of per-turn deltas and compaction
/// checkpoints (<see cref="ConversationEntry"/>). It is BOTH the model's continuity memory AND the
/// durable, examinable record for self-improvement — one log, never trimmed, never overwritten.
/// <para>
/// Storage is the full history; what the model replays is a <em>projection</em>: the latest checkpoint
/// summary plus the user/assistant text of the turns after it (<see cref="GetModelContext"/>).
/// Compaction is non-destructive — it appends a checkpoint (<see cref="AddCheckpoint"/>), it does not
/// erase prior turns. The system prompt is NOT stored here; it is rebuilt fresh each turn.
/// </para>
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// One <see cref="ConversationSummary"/> per conversation under <paramref name="scopeKey"/> — the
    /// key itself and every conversation whose id begins with <c><paramref name="scopeKey"/>:</c> (its
    /// per-chat children), most-recently-active first. This is the reverse-path index: a surface passes
    /// its per-user scope (e.g. <c>web:{userId}</c>) to enumerate that user's chats for a history list,
    /// without loading any transcript. The <c>:</c> boundary keeps <c>web:{user}</c> from matching a
    /// different <c>web:{user2}</c> whose id merely shares a prefix. Empty when nothing matches.
    /// </summary>
    IReadOnlyList<ConversationSummary> ListConversations(string scopeKey);

    /// <summary>
    /// The full conversation history (turns and checkpoints), oldest first — for display and analysis.
    /// Empty when the conversation is unknown.
    /// </summary>
    IReadOnlyList<ConversationEntry> GetHistory(string conversationId);

    /// <summary>
    /// The messages the model should replay as context: the latest checkpoint's summary (if any) as a
    /// leading assistant message, then the user prompt + final reply of every turn after it (or the
    /// whole conversation when there is no checkpoint). Tool/thinking detail is never replayed.
    /// </summary>
    IReadOnlyList<LlmMessage> GetModelContext(string conversationId);

    /// <summary>Appends one completed turn (the canonical per-turn delta) to the history log.</summary>
    void AppendTurn(ConversationTurnRecord turn);

    /// <summary>
    /// Appends a compaction checkpoint carrying <paramref name="summary"/>. Non-destructive: prior
    /// turns remain in the history; subsequent <see cref="GetModelContext"/> replays from here forward.
    /// </summary>
    void AddCheckpoint(string conversationId, string summary);
}
