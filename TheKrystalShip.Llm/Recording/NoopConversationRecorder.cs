using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Recording;

/// <summary>
/// The default <see cref="IConversationRecorder"/>: records nothing. Registered when recording is
/// disabled, so the agent loop's <see cref="IConversationRecorder.Enabled"/> check short-circuits
/// and no per-turn record is ever built.
/// </summary>
public sealed class NoopConversationRecorder : IConversationRecorder
{
    public bool Enabled => false;

    public void Record(ConversationTurnRecord record)
    {
        // Intentionally empty.
    }
}
