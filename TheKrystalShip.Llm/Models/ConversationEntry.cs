namespace TheKrystalShip.Llm.Models;

/// <summary>What a <see cref="ConversationEntry"/> carries.</summary>
public enum ConversationEntryKind
{
    /// <summary>A completed model↔tool turn (a <see cref="ConversationTurnRecord"/>).</summary>
    Turn,

    /// <summary>A compaction checkpoint: a summary of the conversation up to this point.</summary>
    Checkpoint
}

/// <summary>
/// One element of a conversation's append-only history log. Entries are per-turn <i>deltas</i> (a
/// turn's own content, or a checkpoint summary) appended in order — never updated, trimmed, or
/// replaced. The full conversation is the ordered sequence of entries; the model context is the
/// replay of entries from the latest <see cref="ConversationEntryKind.Checkpoint"/> forward.
/// </summary>
public sealed record ConversationEntry
{
    public required ConversationEntryKind Kind { get; init; }

    /// <summary>When the entry was appended (turn completion, or checkpoint creation).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The turn payload when <see cref="Kind"/> is <see cref="ConversationEntryKind.Turn"/>; else <c>null</c>.</summary>
    public ConversationTurnRecord? Turn { get; init; }

    /// <summary>The summary when <see cref="Kind"/> is <see cref="ConversationEntryKind.Checkpoint"/>; else <c>null</c>.</summary>
    public string? CheckpointSummary { get; init; }

    public static ConversationEntry ForTurn(ConversationTurnRecord turn) =>
        new() { Kind = ConversationEntryKind.Turn, CreatedAt = turn.CompletedAt, Turn = turn };

    public static ConversationEntry ForCheckpoint(string summary, DateTimeOffset createdAt) =>
        new() { Kind = ConversationEntryKind.Checkpoint, CreatedAt = createdAt, CheckpointSummary = summary };
}
