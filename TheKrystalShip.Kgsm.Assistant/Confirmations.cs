namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>The kind of destructive operation awaiting confirmation.</summary>
public enum ConfirmationKind
{
    Uninstall,
    Install
}

/// <summary>
/// A destructive operation that has been resolved and staged, awaiting an
/// explicit human confirmation before it runs.
/// <para>
/// <see cref="Target"/> is the RESOLVED name (an existing instance for
/// <see cref="ConfirmationKind.Uninstall"/>, a known blueprint for
/// <see cref="ConfirmationKind.Install"/>) — never the model's raw argument.
/// </para>
/// </summary>
public sealed record PendingConfirmation(
    ConfirmationKind Kind,
    string Target,
    string? InstanceName = null);

/// <summary>
/// Ambient, per-turn sink for destructive operations staged during an agent run.
/// <para>
/// The library agent loop calls the tool dispatcher with only the tool call (no
/// per-turn context), so the dispatcher publishes staged confirmations here and
/// <see cref="ServerAssistant"/> drains them after the turn — without the library
/// needing any notion of "confirmation". Backed by an <see cref="System.Threading.AsyncLocal{T}"/>
/// so concurrently-handled turns stay isolated.
/// </para>
/// </summary>
public interface IConfirmationContext
{
    /// <summary>
    /// Starts a fresh per-turn scope. Dispose to clear it. Read <see cref="IConfirmationScope.Staged"/>
    /// off the returned scope to drain what was staged — that reads the live backing list by
    /// reference, so it stays correct even when the ambient context is lost across the
    /// <c>yield</c>s of an async-streaming turn (where <see cref="Staged"/> would read empty).
    /// </summary>
    IConfirmationScope BeginTurn();

    /// <summary>Records a staged confirmation for the current turn (no-op outside a turn).</summary>
    void Stage(PendingConfirmation confirmation);

    /// <summary>
    /// The confirmations staged in the CURRENT ambient turn (empty outside one). Valid for a
    /// synchronous drain right after the turn; for a streaming turn read the scope's
    /// <see cref="IConfirmationScope.Staged"/> instead (this one is lost across yields).
    /// </summary>
    IReadOnlyList<PendingConfirmation> Staged { get; }
}

/// <summary>A per-turn confirmation scope. Its <see cref="Staged"/> reads the turn's live list.</summary>
public interface IConfirmationScope : IDisposable
{
    /// <summary>A snapshot of the ops staged during this turn, read from the live backing list.</summary>
    IReadOnlyList<PendingConfirmation> Staged { get; }
}

/// <inheritdoc />
public sealed class ConfirmationContext : IConfirmationContext
{
    private static readonly AsyncLocal<List<PendingConfirmation>?> Current = new();

    public IConfirmationScope BeginTurn()
    {
        var list = new List<PendingConfirmation>();
        Current.Value = list;
        return new Scope(list);
    }

    public void Stage(PendingConfirmation confirmation) => Current.Value?.Add(confirmation);

    public IReadOnlyList<PendingConfirmation> Staged =>
        Current.Value is { } list ? list.ToArray() : Array.Empty<PendingConfirmation>();

    private sealed class Scope : IConfirmationScope
    {
        // Holds the SAME list instance the dispatcher stages into (via the AsyncLocal it was set
        // as). Reading it here is by reference, so it survives async-iterator yields that drop the
        // ambient AsyncLocal value. ToArray gives the caller an immutable snapshot.
        private readonly List<PendingConfirmation> _staged;

        public Scope(List<PendingConfirmation> staged) => _staged = staged;

        public IReadOnlyList<PendingConfirmation> Staged => _staged.ToArray();

        public void Dispose() => Current.Value = null;
    }
}
