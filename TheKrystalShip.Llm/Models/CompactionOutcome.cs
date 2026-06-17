namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The result of a compaction request. <see cref="Compacted"/> is false when there was nothing
/// worth compacting (an empty or single-message conversation), in which case the history is left
/// untouched. On success it carries how many messages were folded away and the summary text
/// that replaced them (handy for a one-line confirmation to the user).
/// </summary>
public sealed record CompactionOutcome(bool Compacted, int MessagesCompacted, string Summary)
{
    /// <summary>Nothing to compact — the history was left untouched.</summary>
    public static CompactionOutcome Nothing() => new(false, 0, string.Empty);

    /// <summary>The history was replaced by <paramref name="summary"/>.</summary>
    public static CompactionOutcome Done(int messagesCompacted, string summary) =>
        new(true, messagesCompacted, summary);
}
