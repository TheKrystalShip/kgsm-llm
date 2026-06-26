namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The kind of command awaiting confirmation. Every server command is propose-only
/// (§3.5): the model never executes one, it only stages it here; a human confirms
/// before it runs. New members are APPENDED (never reordered) — the Service mints
/// stateless tokens carrying <c>(int)Kind</c>, so reordering would invalidate any
/// in-flight token.
/// </summary>
public enum ConfirmationKind
{
    // Original destructive tier.
    Uninstall,
    Install,
    // §3.5 generalisation: the formerly-inline commands, now propose-only too.
    Start,
    Stop,
    Restart,
    Update,
    Backup,
    // §3.8: set a single key=value in an instance's .config.ini. Propose-only like the
    // rest; carries a key/value payload and has its own confirm path (like Install).
    SetConfig
}

/// <summary>
/// Classification + human-readable labels for <see cref="ConfirmationKind"/>. Shared by
/// the dispatcher (staging message), the assistant (confirm/execute), and the surfaces
/// (prompt text + future confirm-friction).
/// </summary>
public static class ConfirmationKinds
{
    /// <summary>
    /// Instance-targeted command verbs that stage → confirm → execute via the matching
    /// single-instance <c>IServerOperations</c> op. (<see cref="ConfirmationKind.Install"/>
    /// is excluded — it creates a NEW instance from a blueprint, so it carries a different
    /// payload and has its own confirm path.)
    /// </summary>
    public static readonly IReadOnlySet<ConfirmationKind> Commands = new HashSet<ConfirmationKind>
    {
        ConfirmationKind.Start, ConfirmationKind.Stop, ConfirmationKind.Restart,
        ConfirmationKind.Update, ConfirmationKind.Backup, ConfirmationKind.Uninstall,
    };

    /// <summary>
    /// Kinds that destroy data / are irreversible. V1 confirm friction is uniform
    /// (Confirm/Cancel for all, Q5); this set is the home for the stronger
    /// type-the-name friction those kinds will get later, so renderers can already
    /// emphasise them differently.
    /// </summary>
    public static readonly IReadOnlySet<ConfirmationKind> Destructive = new HashSet<ConfirmationKind>
    {
        ConfirmationKind.Uninstall,
    };

    public static bool IsDestructive(ConfirmationKind kind) => Destructive.Contains(kind);

    /// <summary>Imperative verb label, e.g. "start", "restart", "back up".</summary>
    public static string Verb(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Start => "start",
        ConfirmationKind.Stop => "stop",
        ConfirmationKind.Restart => "restart",
        ConfirmationKind.Update => "update",
        ConfirmationKind.Backup => "back up",
        ConfirmationKind.Uninstall => "uninstall",
        ConfirmationKind.Install => "install",
        ConfirmationKind.SetConfig => "set config on",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Past-tense outcome label, e.g. "started", "backed up".</summary>
    public static string PastTense(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Start => "started",
        ConfirmationKind.Stop => "stopped",
        ConfirmationKind.Restart => "restarted",
        ConfirmationKind.Update => "updated",
        ConfirmationKind.Backup => "backed up",
        ConfirmationKind.Uninstall => "uninstalled",
        ConfirmationKind.Install => "installed",
        ConfirmationKind.SetConfig => "reconfigured",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// A command that has been resolved and staged, awaiting an explicit human
/// confirmation before it runs.
/// <para>
/// <see cref="Target"/> is the RESOLVED name (an existing instance for the
/// instance-targeted kinds, a known blueprint for <see cref="ConfirmationKind.Install"/>)
/// — never the model's raw argument. <see cref="InstanceName"/> is Install-only (the
/// optional custom name for the new instance). <see cref="ConfigKey"/>/<see cref="ConfigValue"/>
/// are <see cref="ConfirmationKind.SetConfig"/>-only (the config key to set and its new value;
/// the value may legitimately be the empty string).
/// </para>
/// </summary>
public sealed record PendingConfirmation(
    ConfirmationKind Kind,
    string Target,
    string? InstanceName = null,
    string? ConfigKey = null,
    string? ConfigValue = null);

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
    /// <para>
    /// <paramref name="autoExecute"/> marks this turn as auto-accept (the api verified the caller is
    /// an admin who turned the toggle on): the dispatcher then RUNS a lifecycle command immediately
    /// instead of staging it for confirmation. Read it back via <see cref="AutoExecute"/>. Like the
    /// staged list, it is ambient for the turn (set here, read by the dispatcher mid-run).
    /// </para>
    /// </summary>
    IConfirmationScope BeginTurn(bool autoExecute = false);

    /// <summary>Records a staged confirmation for the current turn (no-op outside a turn).</summary>
    void Stage(PendingConfirmation confirmation);

    /// <summary>
    /// The confirmations staged in the CURRENT ambient turn (empty outside one). Valid for a
    /// synchronous drain right after the turn; for a streaming turn read the scope's
    /// <see cref="IConfirmationScope.Staged"/> instead (this one is lost across yields).
    /// </summary>
    IReadOnlyList<PendingConfirmation> Staged { get; }

    /// <summary>
    /// Whether the CURRENT ambient turn is auto-accept (admin + toggle, decided by the api). Read by
    /// the dispatcher to choose run-now vs stage-for-confirmation. False outside a turn, and false
    /// for any turn started without it — so a path that doesn't opt in can never auto-run.
    /// </summary>
    bool AutoExecute { get; }
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
    private static readonly AsyncLocal<bool> CurrentAutoExecute = new();

    public IConfirmationScope BeginTurn(bool autoExecute = false)
    {
        var list = new List<PendingConfirmation>();
        Current.Value = list;
        CurrentAutoExecute.Value = autoExecute;
        return new Scope(list);
    }

    public void Stage(PendingConfirmation confirmation) => Current.Value?.Add(confirmation);

    public IReadOnlyList<PendingConfirmation> Staged =>
        Current.Value is { } list ? list.ToArray() : Array.Empty<PendingConfirmation>();

    public bool AutoExecute => CurrentAutoExecute.Value;

    private sealed class Scope : IConfirmationScope
    {
        // Holds the SAME list instance the dispatcher stages into (via the AsyncLocal it was set
        // as). Reading it here is by reference, so it survives async-iterator yields that drop the
        // ambient AsyncLocal value. ToArray gives the caller an immutable snapshot.
        private readonly List<PendingConfirmation> _staged;

        public Scope(List<PendingConfirmation> staged) => _staged = staged;

        public IReadOnlyList<PendingConfirmation> Staged => _staged.ToArray();

        public void Dispose()
        {
            Current.Value = null;
            CurrentAutoExecute.Value = false;
        }
    }
}
