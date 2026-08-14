namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Configuration options for conversation memory.
/// </summary>
public class ConversationOptions
{
    public const string Section = "Conversation";

    /// <summary>
    /// Filesystem path to the SQLite database that holds the canonical conversation history
    /// (<see cref="SqliteConversationStore"/>). A host points this at its durable state dir (the deployed
    /// Service uses its state directory). When null/blank the store defaults to a file beside the host
    /// binary, so it always has a home.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// How full the context window may get, as a percentage, before a finished turn is compacted
    /// automatically. 0 switches it off and leaves compaction entirely manual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left to a person, this never happens.</b> A conversation nobody compacts grows until the
    /// backend quietly drops the front of it, and the first sign is the assistant having forgotten
    /// something nobody told it to forget. A room keyed to a channel makes that certain rather than
    /// likely: it is one conversation for the life of the place, and the people speaking into it have
    /// no reason to know a context window exists.
    /// </para>
    /// <para>
    /// Below 100 by a wide margin on purpose. The trigger is measured on the turn that just finished,
    /// so the next one has to fit in what is left — and it carries a fresh system prompt, the injected
    /// server lists and whatever tool output it pulls. Compacting at the brim would leave the turn that
    /// discovers the problem no room to answer in.
    /// </para>
    /// </remarks>
    public int CompactAtPercent { get; set; } = 70;

    // NOTE: there is deliberately no rolling-window or idle-timeout knob. The history is the append-only
    // canon (full, never trimmed); compaction bounds the MODEL context via checkpoints, not by deleting
    // history — and a checkpoint carrying no summary is how a conversation starts over without moving to
    // a new id. See SqliteConversationStore / ConversationCompactor.
}
