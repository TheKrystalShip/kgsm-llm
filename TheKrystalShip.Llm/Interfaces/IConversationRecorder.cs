using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// An append-only sink for completed turns, captured for offline self-improvement analysis (which
/// tool the model picked, with what args, did it error, how many round-trips, which system prompt) —
/// a separate concern from <see cref="IConversationStore"/>, which is the model's lossy rolling
/// working memory. The store trims, resets on idle, and is overwritten by compaction; this recorder
/// never loses a turn. Installed once at the agent loop so every surface inherits it.
/// </summary>
public interface IConversationRecorder
{
    /// <summary>
    /// Whether this recorder will do anything. The agent loop checks this to skip building the
    /// per-turn record entirely when recording is off, keeping the disabled path allocation-free.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Records one completed turn. Implementations MUST be failure-isolated (a write error is
    /// swallowed/logged, never propagated) and self-flushing per call — a one-shot CLI invocation
    /// records its single turn and exits immediately, so nothing may rely on later disposal to flush.
    /// </summary>
    void Record(ConversationTurnRecord record);
}
