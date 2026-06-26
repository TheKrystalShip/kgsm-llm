namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Configuration options for conversation memory.
/// </summary>
public class ConversationOptions
{
    public const string Section = "Conversation";

    /// <summary>
    /// Maximum number of messages (user + assistant combined) kept per
    /// conversation. Older messages are dropped. Keeps context well under num_ctx.
    /// </summary>
    public int MaxMessages { get; set; } = 12;

    /// <summary>
    /// Filesystem path to the SQLite database that holds conversation memory
    /// (<see cref="SqliteConversationStore"/>). A host points this at its durable state dir (the deployed
    /// Service uses its state directory). When null/blank the store defaults to a file beside the host
    /// binary, so it always has a home.
    /// </summary>
    public string? DatabasePath { get; set; }

    // NOTE: there is deliberately no idle-timeout / reset knob. The conversation id is the canonical
    // scope (a fresh chat = a fresh id), so conversations are retained and resumable by id — see
    // SqliteConversationStore.
}
