namespace TheKrystalShip.Llm.Models;

/// <summary>
/// One user's whole footprint in the conversation log — the row
/// <see cref="Interfaces.IConversationStore.ListActors"/> returns, so a surface can offer "whose
/// conversations do you want to read" without loading a single transcript.
/// <para>
/// An actor is a <c>surface:user</c> namespace, derived from the ids already in the log rather than
/// from any registry the store keeps: conversation ids are <c>{surface}:{user}[:{chat}]</c>, so every
/// conversation belongs to exactly one actor and the set of actors IS the set of distinct
/// <c>{surface}:{user}</c> prefixes. Pass <see cref="UserId"/> back as the scope key
/// (<c>{Surface}:{UserId}</c>) to list that actor's conversations.
/// </para>
/// </summary>
public sealed record ConversationActor
{
    /// <summary>The surface the conversations were held through (e.g. <c>web</c>).</summary>
    public required string Surface { get; init; }

    /// <summary>
    /// The opaque per-surface user segment of the id (a Discord snowflake on the web surface). It is
    /// an identifier, not a name — see <see cref="UserDisplay"/>.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The newest display name recorded on any of this actor's turns, or <c>null</c> when none of them
    /// carries one (turns recorded before the host supplied a name). A reader shows
    /// <see cref="UserId"/> in that case — a name is never inferred from an id.
    /// </summary>
    public string? UserDisplay { get; init; }

    /// <summary>How many conversations the actor holds, <b>including</b> soft-deleted ones.</summary>
    public required int ConversationCount { get; init; }

    /// <summary>How many of <see cref="ConversationCount"/> are soft-deleted (hidden from the actor's own list).</summary>
    public required int DeletedCount { get; init; }

    /// <summary>Completed turns across all of the actor's conversations (checkpoints excluded).</summary>
    public required int TurnCount { get; init; }

    /// <summary>When the actor's oldest entry was appended.</summary>
    public required DateTimeOffset FirstActivityAt { get; init; }

    /// <summary>When the actor's newest entry was appended (drives recency ordering).</summary>
    public required DateTimeOffset LastActivityAt { get; init; }
}
