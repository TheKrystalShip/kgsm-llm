namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Configuration for durable memory — what the assistant writes down in one conversation and reads
/// back in later ones.
/// </summary>
/// <remarks>
/// There is deliberately no <c>DatabasePath</c> here. Memories live in the same SQLite file as the
/// conversation history (<see cref="ConversationOptions.DatabasePath"/>), alongside the pending
/// confirmations and the session registry — one file is one thing to back up, and a second path would
/// let a host point the two halves of the same assistant's state at different disks.
/// </remarks>
public class MemoryOptions
{
    public const string Section = "Memory";

    /// <summary>
    /// The most memories one owner may hold. Every one of them costs a line in every system prompt
    /// that owner's turns are built from, so this is a context budget rather than a storage limit.
    /// </summary>
    /// <remarks>
    /// A write past the cap is <b>refused</b>, naming the cap. It is not an eviction: dropping the
    /// oldest would silently discard something a person asked to be remembered, and the assistant
    /// would go on believing it had been kept.
    /// </remarks>
    public int MaxPerOwner { get; set; } = 64;

    /// <summary>
    /// The longest a memory's one-line summary may be. This is the line injected into every turn, so
    /// the bound is what keeps a full memory store from crowding out the prompt around it.
    /// </summary>
    public int MaxSummaryLength { get; set; } = 200;

    /// <summary>
    /// The longest a memory's body may be. Read only on demand, so it can afford to be far larger
    /// than the summary — but not unbounded, because one recall of it lands in the model's context.
    /// </summary>
    public int MaxBodyLength { get; set; } = 2000;
}
