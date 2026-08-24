namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The kind of command awaiting confirmation. Every server command is propose-only
///: the model never executes one, it only stages it here; a human confirms
/// before it runs.
/// <para>
/// <b>Every member's number is written out and is permanent.</b> The Service persists
/// <c>(int)Kind</c> against a staged action, so the number — not the position — is what a
/// pending row redeems as. A member that goes away leaves its number behind as a hole rather
/// than letting the ones after it shift down onto rows already written: a staged file write
/// coming back as a blueprint finalize is the failure that buys. A retired number is never
/// reused, and a row still carrying one fails <c>Enum.IsDefined</c> at redemption and is
/// refused, which is the right answer for a staged action this build can no longer perform.
/// </para>
/// </summary>
public enum ConfirmationKind
{
    // Original destructive tier.
    Uninstall = 0,
    Install = 1,
    // Every lifecycle verb is propose-only.
    Start = 2,
    Stop = 3,
    Restart = 4,
    Update = 5,
    Backup = 6,
    // Set a single key=value in an instance's .config.ini. Propose-only like the
    // rest; carries a key/value payload and has its own confirm path (like Install).
    SetConfig = 7,
    //
    // 8 is retired and must not be reused. It staged an on-demand host-firewall open; an
    // instance's ports are now opened by the supervisor when it starts and released when it
    // stops, so there is no port for a person — or a model — to open by hand.
    //
    // Overwrite a GAME's own config file (as opposed to KGSM's .config.ini — that's SetConfig).
    // Propose-only like the rest; carries the relative path on ConfigKey and the COMPLETE new
    // content on ConfigValue, and has its own confirm path. Not Destructive (a .kgsmbak backup
    // + the confirm-time preview are the friction, not a type-the-name gate).
    WriteFile = 9,
    // Finalize an assistant-authored blueprint the user reviewed/edited in the chat (the mandatory
    // human-review checkpoint). Unlike the rest, "confirm" runs a long pipeline
    // (test-install → verify → repair → keep) and its RESULT is a rich card, not a one-line outcome — so it
    // has its own confirm entrypoint that returns a BlueprintAuthoringData rather than ConfirmAsync's text.
    // Carries the resolved slug on Target, the game display name on InstanceName, and the DRAFT YAML on
    // ConfigValue (the fallback body; the user's Save sends the possibly-edited YAML alongside the token).
    Blueprint = 10,
    //
    // Acting on an instance's EXISTING backups. Restore replaces the server's current data and Delete
    // is permanent, so both are Destructive; Prune deletes only what retention already marks as
    // surplus. The backup id rides on ConfigKey, and Prune's keep-count on ConfigValue.
    BackupRestore = 11,
    BackupDelete = 12,
    BackupPrune = 13,
    //
    // Player moderation. The target rides on ConfigKey; the engine decides whether the game addresses
    // a name or an id, so nothing here converts between the two.
    PlayerKick = 14,
    PlayerBan = 15,
    PlayerUnban = 16,
    //
    // Boot autostart intent, held by the supervisor rather than in the instance's .config.ini. Distinct
    // from Start/Stop: these change what happens at the NEXT boot and touch the running server not at all.
    AutostartEnable = 17,
    AutostartDisable = 18
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
        // Restoring overwrites the server's live data with an older copy, and deleting a backup is
        // gone-for-good. Both destroy something a person cannot get back, which is what this set means.
        ConfirmationKind.BackupRestore,
        ConfirmationKind.BackupDelete,
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
        ConfirmationKind.WriteFile => "write to a file on",
        ConfirmationKind.Blueprint => "test-install and add",
        ConfirmationKind.BackupRestore => "restore a backup onto",
        ConfirmationKind.BackupDelete => "delete a backup of",
        ConfirmationKind.BackupPrune => "prune old backups of",
        ConfirmationKind.PlayerKick => "kick a player from",
        ConfirmationKind.PlayerBan => "ban a player from",
        ConfirmationKind.PlayerUnban => "unban a player on",
        ConfirmationKind.AutostartEnable => "enable boot autostart for",
        ConfirmationKind.AutostartDisable => "disable boot autostart for",
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
        ConfirmationKind.WriteFile => "had a file updated",
        ConfirmationKind.Blueprint => "added to the catalog",
        ConfirmationKind.BackupRestore => "restored from a backup",
        ConfirmationKind.BackupDelete => "had a backup deleted",
        ConfirmationKind.BackupPrune => "had its old backups pruned",
        ConfirmationKind.PlayerKick => "had a player kicked",
        ConfirmationKind.PlayerBan => "had a player banned",
        ConfirmationKind.PlayerUnban => "had a player unbanned",
        ConfirmationKind.AutostartEnable => "set to start at boot",
        ConfirmationKind.AutostartDisable => "set not to start at boot",
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
/// are overloaded per kind: for <see cref="ConfirmationKind.SetConfig"/> they are the config
/// key/value (the value may
/// legitimately be the empty string); for <see cref="ConfirmationKind.WriteFile"/> they are the
/// file's relative path (<see cref="ConfigKey"/>) and its COMPLETE new content
/// (<see cref="ConfigValue"/>). Both surfaces carry the real content here — the CLI in-process for
/// the length of one prompt, the Service in its pending-confirmation store — because what a client
/// holds is a handle, so there is no size a payload has to fit into.
/// </para>
/// <para>
/// <see cref="Library"/> is Install-only: the library the new instance is placed in, by name. It has
/// a field of its own rather than a third overloaded slot because it is a name the confirming surface
/// shows the person — an install landing on a disk they did not pick is exactly what the confirmation
/// exists to prevent. Null means the engine resolves placement itself.
/// </para>
/// </summary>
public sealed record PendingConfirmation(
    ConfirmationKind Kind,
    string Target,
    string? InstanceName = null,
    string? ConfigKey = null,
    string? ConfigValue = null,
    string? Library = null);

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
    /// Records that this turn RAN something rather than staging it — the auto-accept path, the only
    /// one that acts without leaving a staged confirmation behind. Read back off the scope so a turn
    /// that acted is never mistaken for one that did nothing.
    /// </summary>
    void NoteActionPerformed();

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

    /// <summary>
    /// Whether this turn ran an action outright (the auto-accept path). Read from the live backing
    /// state for the same reason <see cref="Staged"/> is — it stays correct across a streaming
    /// turn's yields.
    /// </summary>
    bool ActionPerformed { get; }
}

/// <inheritdoc />
public sealed class ConfirmationContext : IConfirmationContext
{
    private static readonly AsyncLocal<TurnState?> Current = new();
    private static readonly AsyncLocal<bool> CurrentAutoExecute = new();

    public IConfirmationScope BeginTurn(bool autoExecute = false)
    {
        var state = new TurnState();
        Current.Value = state;
        CurrentAutoExecute.Value = autoExecute;
        return new Scope(state);
    }

    public void Stage(PendingConfirmation confirmation) => Current.Value?.Staged.Add(confirmation);

    public void NoteActionPerformed()
    {
        if (Current.Value is { } state)
            state.ActionPerformed = true;
    }

    public IReadOnlyList<PendingConfirmation> Staged =>
        Current.Value is { } state ? state.Staged.ToArray() : Array.Empty<PendingConfirmation>();

    public bool AutoExecute => CurrentAutoExecute.Value;

    /// <summary>
    /// What the dispatcher records about the turn in progress. Held BY REFERENCE by the scope, so
    /// both fields survive the async-iterator yields that drop the ambient AsyncLocal value.
    /// </summary>
    private sealed class TurnState
    {
        public List<PendingConfirmation> Staged { get; } = [];

        public bool ActionPerformed { get; set; }
    }

    private sealed class Scope : IConfirmationScope
    {
        // Holds the SAME state instance the dispatcher writes into (via the AsyncLocal it was set
        // as). Reading it here is by reference, so it survives async-iterator yields that drop the
        // ambient AsyncLocal value. ToArray gives the caller an immutable snapshot.
        private readonly TurnState _state;

        public Scope(TurnState state) => _state = state;

        public IReadOnlyList<PendingConfirmation> Staged => _state.Staged.ToArray();

        public bool ActionPerformed => _state.ActionPerformed;

        public void Dispose()
        {
            Current.Value = null;
            CurrentAutoExecute.Value = false;
        }
    }
}
