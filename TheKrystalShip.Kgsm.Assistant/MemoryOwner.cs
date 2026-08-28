namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Ambient, per-turn record of <b>whose</b> memory this turn may read and write.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="SearchIntent"/>'s and <see cref="IConfirmationContext"/>'s ambient-scope shape,
/// for the same reason: the owner is known when the turn starts and needed deep inside tool dispatch,
/// with nothing between the two to thread it through. Backed by an <see cref="AsyncLocal{T}"/>, so
/// concurrent turns stay isolated.
/// </para>
/// <para>
/// <b>Open this in the yield-free flow the dispatcher runs on</b> — beside the confirmation,
/// progress and search scopes in <c>ServerAssistant.ProduceStreamAsync</c> — never in the async
/// iterator above it. An iterator's yields drop the ambient value, so a scope opened there would be
/// gone by the first tool call, on the streaming path every surface uses.
/// </para>
/// <para>
/// <b>The owner is derived from the conversation, never from the model.</b> It is deliberately not
/// a tool argument: an owner key the model could name is one it could get wrong, and getting it wrong
/// means writing into somebody else's memory. Outside a turn — or on a turn whose conversation id
/// resolved to nothing — this is null and every memory tool refuses.
/// </para>
/// </remarks>
public static class MemoryOwner
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>
    /// The owner key this turn's memory tools act on, or <see langword="null"/> when there is none —
    /// which the dispatcher reports as a refusal rather than falling back to a shared namespace.
    /// </summary>
    public static string? Key => string.IsNullOrEmpty(Current.Value) ? null : Current.Value;

    /// <summary>Opens a scope for one turn. Dispose to clear it.</summary>
    public static IDisposable BeginTurn(string? ownerKey)
    {
        Current.Value = ownerKey;
        return new Turn();
    }

    private sealed class Turn : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
