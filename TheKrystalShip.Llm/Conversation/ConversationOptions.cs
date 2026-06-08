namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Configuration options for short-term conversation memory.
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
    /// Minutes of inactivity after which a conversation is reset, so unrelated
    /// chats hours apart don't leak context into one another.
    /// </summary>
    public int IdleTimeoutMinutes { get; set; } = 15;
}
