using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// An in-memory <see cref="IConversationRecorder"/> that captures each turn's full trajectory
/// (the model's tool calls, iteration count, outcome, and the system-prompt hash) <b>without</b>
/// writing to the user's real transcript corpus on disk. This is the only seam that exposes the
/// tool trajectory — <see cref="IServerAssistant.RunAsync"/> returns just the reply text and staged
/// confirmations — so the harness swaps this in (via <c>RemoveAll</c>) and reads <see cref="Last"/>
/// after each turn to score it.
/// <para>
/// <see cref="Enabled"/> is true so the agent loop actually records. The harness drives one turn at
/// a time on a fresh conversation id per rep, so a single-slot capture is sufficient; the lock just
/// guards against any internal background completion path.
/// </para>
/// </summary>
internal sealed class CapturingRecorder : IConversationRecorder
{
    private readonly object _gate = new();
    private ConversationTurnRecord? _last;

    public bool Enabled => true;

    public void Record(ConversationTurnRecord record)
    {
        lock (_gate) _last = record;
    }

    /// <summary>The most recently recorded turn, or null if nothing was recorded since the last reset.</summary>
    public ConversationTurnRecord? Last
    {
        get { lock (_gate) return _last; }
    }

    /// <summary>Clear the slot before a turn so a missing record surfaces as null rather than stale data.</summary>
    public void Reset()
    {
        lock (_gate) _last = null;
    }
}
