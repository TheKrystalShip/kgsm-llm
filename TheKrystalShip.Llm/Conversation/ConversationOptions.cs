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

    // NOTE: there is deliberately no rolling-window or idle-timeout knob. The history is the append-only
    // canon (full, never trimmed); the conversation id is the scope (a fresh chat = a fresh id) and
    // compaction bounds the MODEL context via checkpoints, not by deleting history. See
    // SqliteConversationStore / ConversationCompactor.
}
