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

    // NOTE: there is deliberately no idle-timeout / reset knob. The conversation id is the canonical
    // scope (a fresh chat = a fresh id), so conversations are retained for the process lifetime and
    // resumable from any past point — see InMemoryConversationStore.
}
