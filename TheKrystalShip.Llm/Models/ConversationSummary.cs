namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A one-line index entry for a stored conversation — the shape <see cref="Interfaces.IConversationStore.ListConversations"/>
/// returns so a surface can render a "your past chats" list without loading every transcript. Derived
/// cheaply from the append-only log: <see cref="Title"/> is what it is called (see
/// <see cref="ConversationTitle"/>), the timestamps bound the log, and
/// <see cref="TurnCount"/> is how many turns it holds. The full transcript is fetched separately by
/// <see cref="Interfaces.IConversationStore.GetHistory"/>.
/// </summary>
public sealed record ConversationSummary
{
    /// <summary>The full stored conversation id (e.g. <c>web:{userId}:{chatId}</c>).</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// What the conversation is called — the first turn's prompt, single-lined and length-capped, or
    /// <see cref="ConversationTitle.NewConversation"/> while it holds no turn. Never null: a name is the
    /// store's to give, and a null here is a word each surface would have to invent separately.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>When the conversation's first entry was appended.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the conversation's most recent entry was appended (drives recency ordering).</summary>
    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>Number of completed turns in the conversation (checkpoints excluded).</summary>
    public required int TurnCount { get; init; }

    /// <summary>
    /// The newest display name recorded on any of the conversation's turns; <c>null</c> when no turn
    /// carries one (<see cref="ConversationTurnRecord.UserDisplay"/>). Never derived from the id.
    /// </summary>
    public string? UserDisplay { get; init; }

    /// <summary>
    /// Whether the conversation is soft-deleted — hidden from its owner's own list while every turn
    /// stays in the log. Always <c>false</c> in a listing that excludes them.
    /// </summary>
    public bool Deleted { get; init; }

    /// <summary>Turns that failed in the backend (<see cref="TurnOutcome.Error"/>).</summary>
    public int ErrorTurns { get; init; }

    /// <summary>Turns that exhausted the iteration cap without a final answer (<see cref="TurnOutcome.CapHit"/>).</summary>
    public int CapHitTurns { get; init; }

    /// <summary>Turns that ran to completion and produced no answer at all (<see cref="TurnOutcome.Empty"/>).</summary>
    public int EmptyTurns { get; init; }

    /// <summary>
    /// Turns whose owner marked the answer unhelpful — what makes a conversation worth reading. Counted
    /// from the verdict that currently stands, so a thumbs-down later changed or cleared stops counting.
    /// </summary>
    public int NegativeTurns { get; init; }

    /// <summary>
    /// The switches standing on the conversation, as stored — a null field means nothing has ever set
    /// it, which the caller resolves against its own default exactly as
    /// <see cref="Interfaces.IConversationStore.GetPreferences"/> would. Carried on the index so one
    /// listing answers what every conversation is set to: a surface that shows the switches then has
    /// them for the chat it opens without a second read, and cannot show a value it cached earlier.
    /// </summary>
    public ConversationPreferences Preferences { get; init; } = ConversationPreferences.Unset;
}
