using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Compresses a conversation's stored history into a single summary message — preserving
/// continuity while freeing context. The programmatic analogue of a "/compact" command, and
/// surface-agnostic: any host (CLI REPL, Discord bot, web session) can offer it over the same
/// <see cref="IConversationStore"/>.
/// </summary>
public interface IConversationCompactor
{
    /// <summary>
    /// Summarizes the conversation identified by <paramref name="conversationId"/> and replaces
    /// its history with that summary. A no-op (returning <see cref="CompactionOutcome.Nothing"/>)
    /// when there is too little history to be worth a model round-trip. Returns a failure if the
    /// model call fails; the stored history is left untouched on failure or cancellation.
    /// </summary>
    Task<Result<CompactionOutcome>> CompactAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}
