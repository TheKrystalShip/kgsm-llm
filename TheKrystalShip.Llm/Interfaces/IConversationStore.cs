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
    /// <para>
    /// Soft-deleted conversations are excluded unless <paramref name="includeDeleted"/> is set — a
    /// review surface asks for them (they are retained in full, and a hidden one is exactly what a
    /// tuning review wants to see), a user's own history list does not.
    /// </para>
    /// </summary>
    IReadOnlyList<ConversationSummary> ListConversations(string scopeKey, bool includeDeleted = false);

    /// <summary>
    /// One <see cref="ConversationActor"/> per distinct <c>{surfacePrefix}:{user}</c> namespace in the
    /// log, most-recently-active first — the reverse of <see cref="ListConversations"/>, which needs a
    /// scope key the caller already knows. This is what lets a review surface enumerate WHO has talked
    /// to the assistant: the store keeps no user registry, so the answer is derived from the ids
    /// themselves. Counts include soft-deleted conversations
    /// (<see cref="ConversationActor.DeletedCount"/> says how many). Empty when the surface has none.
    /// </summary>
    IReadOnlyList<ConversationActor> ListActors(string surfacePrefix);

    /// <summary>
    /// The whole-corpus roll-up for one surface — outcome mix, answer-time distribution, per-tool
    /// behaviour, prompt-version buckets and daily volume — derived on demand from the same
    /// append-only log the transcripts come from, so a figure here can never disagree with the turns
    /// behind it. Soft-deleted conversations are <b>included</b>: their turns are part of what the
    /// assistant actually did, and excluding them would understate the corpus a review is judging.
    /// Distribution figures are null when nothing was measured (see <see cref="ConversationStats"/>).
    /// </summary>
    ConversationStats GetStats(string surfacePrefix);

    /// <summary>
    /// The full conversation history (turns and checkpoints), oldest first — for display and analysis.
    /// Empty when the conversation is unknown.
    /// </summary>
    IReadOnlyList<ConversationEntry> GetHistory(string conversationId);

    /// <summary>
    /// The messages the model should replay as context: the latest checkpoint's summary (if any) as a
    /// leading assistant message, then every turn after it (or the whole conversation when there is no
    /// checkpoint), each projected by <see cref="Conversation.ModelContextProjection"/> — prompt, tool
    /// calls, reply. A past call's output and the model's thinking are never replayed.
    /// <para>
    /// <paramref name="attributeSpeakers"/> prefixes each replayed user message with the display name
    /// recorded against that turn (<see cref="SpeakerAttribution"/>) — for a conversation several
    /// people share, where an unlabelled transcript reads as one person having said all of it. The
    /// caller decides, because whether a conversation is shared is a property of the key it was
    /// opened under, not of what happens to be in it yet: measured from the turns instead, a room in
    /// which only one person has spoken so far would replay unlabelled while the live prompt arrived
    /// labelled, and the model would meet two spellings of the same conversation. Default false
    /// replays exactly as a one-participant conversation always has.
    /// </para>
    /// </summary>
    IReadOnlyList<LlmMessage> GetModelContext(string conversationId, bool attributeSpeakers = false);

    /// <summary>
    /// Appends one completed turn (the canonical per-turn delta) to the history log, returning the
    /// entry id it was stored under — the handle that addresses this turn specifically. The caller
    /// hands that id to the surface so a client can act on the answer it just received (rate it)
    /// without having to guess which turn "the last one" was.
    /// </summary>
    long AppendTurn(ConversationTurnRecord turn);

    /// <summary>
    /// Appends a compaction checkpoint carrying <paramref name="summary"/>. Non-destructive: prior
    /// turns remain in the history; subsequent <see cref="GetModelContext"/> replays from here forward.
    /// </summary>
    void AddCheckpoint(string conversationId, string summary);

    /// <summary>
    /// Starts the conversation over: subsequent <see cref="GetModelContext"/> replays nothing that came
    /// before this point. Non-destructive in exactly the way <see cref="AddCheckpoint"/> is — every turn
    /// stays in the history, and a transcript still shows the whole thing.
    /// </summary>
    /// <remarks>
    /// <b>It is a checkpoint carrying no summary</b>, which is what makes it one mechanism rather than
    /// two: compaction folds the history into a briefing and replays that, this folds it into silence.
    /// <para>
    /// This — not <see cref="SoftDelete"/> — is what clearing a conversation means. A tombstone hides a
    /// conversation from a listing and the model goes on remembering every word of it.
    /// </para>
    /// <para>
    /// For a conversation whose id is <em>derived</em> rather than chosen — a chat room keyed to the
    /// place it happens in — this is also the only way to start fresh: there is no second id to move to,
    /// because the next thing said in that room resolves back to the same key.
    /// </para>
    /// </remarks>
    void Reset(string conversationId);

    /// <summary>
    /// Soft-deletes a conversation: stops it appearing in <see cref="ListConversations"/> WITHOUT erasing
    /// any turn — the append-only history (the self-improvement corpus) is preserved in full. Append-only
    /// and idempotent; a later <see cref="AppendTurn"/> to the same id (a resume) supersedes the
    /// soft-delete and the conversation reappears. <see cref="GetHistory"/> still returns its transcript.
    /// </summary>
    void SoftDelete(string conversationId);

    /// <summary>
    /// Records the owner's verdict on one turn, or clears it when <paramref name="rating"/> is
    /// <c>null</c>. Append-only and latest-wins, like the soft-delete tombstone: re-rating appends,
    /// it never rewrites the turn, and the turn record itself is untouched either way.
    /// <para>
    /// <paramref name="turnId"/> is checked to be a turn <b>of <paramref name="conversationId"/></b>
    /// and <c>false</c> is returned when it is not. Entry ids are sequential across the whole log, so
    /// without that check a caller scoped to its own conversation could still rate someone else's turn
    /// by naming a neighbouring id.
    /// </para>
    /// </summary>
    bool SetTurnFeedback(string conversationId, long turnId, TurnFeedbackRating? rating, string? note);

    /// <summary>
    /// The switches standing on <paramref name="conversationId"/>, resolved latest-wins per field. A
    /// field is null when nothing has ever set it, which the caller reads as "use the configured
    /// default" — never as <c>false</c>. Unknown conversations answer
    /// <see cref="ConversationPreferences.Unset"/> rather than throwing: a conversation that does not
    /// exist yet has set nothing, which is the same answer.
    /// </summary>
    ConversationPreferences GetPreferences(string conversationId);

    /// <summary>
    /// Records a change to one or both switches, as an append. <paramref name="delta"/> is read as a
    /// delta: a null field says nothing about that switch and leaves what stands, so the two can be
    /// set independently without a read-modify-write. A delta that says nothing at all writes nothing.
    /// </summary>
    void SetPreferences(string conversationId, ConversationPreferences delta);

    /// <summary>
    /// Brings a conversation into being before it holds any turn, so it exists, lists, and is
    /// resumable from another device the moment it is started. Returns whether this call created it —
    /// <c>false</c> when the id was already known, which makes starting a conversation idempotent
    /// rather than an error to be handled.
    /// </summary>
    bool CreateConversation(string conversationId);
}
