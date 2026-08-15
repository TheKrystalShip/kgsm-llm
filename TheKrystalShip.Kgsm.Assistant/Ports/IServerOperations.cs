using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The mutating actions and live (non-cached) reads the assistant performs against
/// a running kgsm. The host implements this over whatever it already uses to talk
/// to kgsm (the Discord bot routes through its MediatR handlers; a standalone
/// service can call KGSM.Lib directly). Implementations must not throw — return a
/// failed <see cref="Result"/> instead.
/// <para>
/// install / uninstall NEVER flow through the agent loop: the dispatcher only
/// STAGES them for human confirmation. They live on this port for the confirm step
/// only — <see cref="IServerAssistant.ConfirmAsync"/> calls them after a human has
/// confirmed a staged operation, never the model.
/// </para>
/// </summary>
public interface IServerOperations
{
    Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Live status text for an instance.</summary>
    Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// The configured (blueprint) port spec for an instance, as the canonical UFW-style string
    /// (e.g. <c>"34197/udp"</c>, <c>"27015:27020/tcp,34197/udp"</c>) read straight from kgsm's
    /// structured instance info — the deterministic source for the <c>open_ports</c> tool, so the
    /// model never has to supply or guess ports. Returns a failed <see cref="Result"/> (with an
    /// honest reason) when the instance is unknown or has no ports configured — never a fabricated
    /// spec, and never throws.
    /// </summary>
    Task<Result<string>> GetConfiguredPortsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a UTF-8 text file belonging to an instance, <b>path-bound to the
    /// instance's install directory</b>: the implementation canonicalizes the path
    /// and refuses anything that resolves outside that directory (a <c>..</c> escape
    /// or an out-of-tree symlink). The size read is capped. Read-only. Returns a
    /// failed <see cref="Result"/> if the instance/dir is unknown, the path escapes,
    /// or the file is missing — never throws.
    /// <para>
    /// V1 callers pass only the instance's own <c>&lt;name&gt;.config.ini</c> (the
    /// dispatcher derives that filename from the resolved instance name, so no
    /// model-supplied path segment ever reaches here). The path-binding is
    /// defense-in-depth for when a future whitelist admits model-chosen files.
    /// </para>
    /// </summary>
    Task<Result<string>> ReadInstanceFileAsync(string instance, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one level of an instance's own directory, <b>path-bound to that directory</b>
    /// exactly like <see cref="ReadInstanceFileAsync"/> (same kgsm-resolved boundary, same
    /// <c>..</c>/out-of-tree-symlink refusal). <paramref name="relativeSubdir"/> selects a
    /// subdirectory to list (e.g. <c>logs</c>); null/blank lists the top level. The listing
    /// is one level deep (non-recursive) and bounded in length. Read-only. Returns a failed
    /// <see cref="Result"/> if the instance is unknown, the path escapes, or the target isn't
    /// a directory — never throws. Powers the model-facing <c>list_files</c> discovery tool
    /// (so it can find a file to hand to <c>read_file</c>).
    /// </summary>
    Task<Result<IReadOnlyList<InstanceDirEntry>>> ListInstanceDirectoryAsync(
        string instance, string? relativeSubdir = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds files anywhere under the instance's directory whose name matches a glob
    /// (<c>*</c>/<c>?</c>), or whose path does when the pattern contains a <c>/</c>. Path-bound
    /// exactly like the reads above, and the walk never follows a symlinked directory out.
    /// <para>
    /// This exists because a game's own config lives at a game-specific depth — Palworld's is five
    /// levels down — and discovering it one directory listing at a time costs more agent iterations
    /// than the turn has. Archived copies under a <c>backups</c> directory are excluded: an archived
    /// config is not the file a question about the live server is about.
    /// </para>
    /// </summary>
    Task<Result<InstanceFileMatches>> FindInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the contents of the instance's text files for a regular expression, returning the
    /// matching lines with their paths. Same path-binding and same never-follow-a-symlink walk as
    /// <see cref="FindInstanceFilesAsync"/>; binaries and oversized files are skipped, and archived
    /// copies under a <c>backups</c> directory are excluded.
    /// <para>
    /// The counterpart to finding by name: a caller often knows the SETTING it wants to change but not
    /// which file holds it.
    /// </para>
    /// </summary>
    Task<Result<InstanceContentMatches>> SearchInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null, bool ignoreCase = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live status of the whole fleet in a single kgsm invocation — the bulk read
    /// that replaces fanning a per-instance check across N instances (the cause of
    /// the agent-loop iteration cap on "which servers are running?"). An instance
    /// whose status could not be read appears as
    /// <see cref="FleetStatusAvailability.Unavailable"/> with a reason, so one bad
    /// instance neither sinks the read nor masquerades as "stopped."
    /// </summary>
    Task<Result<IReadOnlyList<FleetStatusEntry>>> GetFleetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Live running-or-not check for an instance. Internal capability — not a
    /// model-facing tool (a per-instance liveness loop is the iteration-cap cause;
    /// the model uses the bulk <see cref="GetFleetStatusAsync"/> instead). Retained
    /// for host/aggregator use.
    /// </summary>
    Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the neutral inputs the <c>run_health_check</c> aggregator needs for one
    /// instance (running-state, recent log lines, update availability, host disk). The
    /// implementation only <b>fetches + maps</b> — it parses KGSM's strings into the
    /// neutral shape but renders no health judgment; all judgment lives once in
    /// <see cref="Health.HealthCheckAggregator"/>. A field whose source is unavailable
    /// is mapped to its absent form (e.g. <see cref="InstanceHealthSnapshot.HostDisk"/>
    /// null + a reason), never fabricated. Returns a failed <see cref="Result"/> only
    /// when the instance itself cannot be read at all — never throws.
    /// </summary>
    Task<Result<InstanceHealthSnapshot>> GetHealthSnapshotAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a new instance from a blueprint. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a staged
    /// install — never from the agent loop.
    /// </summary>
    /// <param name="blueprint">The resolved blueprint name to install from.</param>
    /// <param name="instanceName">Optional custom instance name; null lets kgsm name it.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="version">Optional specific version; null installs the latest.</param>
    /// <param name="port">Optional override of the blueprint's primary game port; null keeps it.</param>
    Task<Result> InstallAsync(
        string blueprint,
        string? instanceName,
        CancellationToken cancellationToken = default,
        string? version = null,
        int? port = null);

    /// <summary>
    /// PERMANENTLY uninstalls an instance and all its data. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a staged
    /// uninstall — never from the agent loop.
    /// </summary>
    /// <param name="instance">The resolved instance name to uninstall.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a single key=value in an instance's <c>.config.ini</c>. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a staged
    /// set-config — never from the agent loop. kgsm owns the safety policy: it refuses
    /// structural/identity/path/toggle keys, surfacing that refusal as a failed
    /// <see cref="Result"/> (this method does not pre-judge the key).
    /// </summary>
    /// <param name="instance">The resolved instance name.</param>
    /// <param name="key">The config key to set.</param>
    /// <param name="value">The new value (may be the empty string, but not null).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<Result> SetInstanceConfigValueAsync(string instance, string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites a text file belonging to an instance with <paramref name="content"/> —
    /// the GAME's own config file (e.g. Palworld's <c>PalWorldSettings.ini</c>), never
    /// KGSM's own <c>.config.ini</c> (that's <see cref="SetInstanceConfigValueAsync"/>).
    /// Called only by <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a
    /// staged write — never from the agent loop. <paramref name="relativePath"/> is
    /// <b>path-bound to the instance's own directory</b> exactly like
    /// <see cref="ReadInstanceFileAsync"/> (same kgsm-resolved boundary, same
    /// <c>..</c>/out-of-tree-symlink refusal); a non-regular target or a new file whose
    /// parent directory doesn't already exist in-jail is refused. The write is capped in
    /// size, atomic (temp file + rename in the same directory), and backs up a non-empty
    /// existing target to a sibling <c>.kgsmbak</c> (overwritten each time — last-good, not
    /// a history) before replacing it. Returns a failed <see cref="Result"/> for any jail
    /// violation, oversized content, or I/O failure — never throws.
    /// </summary>
    Task<Result> WriteInstanceFileAsync(
        string instance, string relativePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a proposed edit into the full new content of <paramref name="relativePath"/>, by
    /// reading the file that is there now and replacing <paramref name="oldText"/> with
    /// <paramref name="newText"/> in it — the read half of a staged write. Read-only: it computes
    /// the content a confirmation would write and touches nothing.
    /// <para>
    /// This is what keeps a file's bytes off the model's round trip. The caller names only the text
    /// that changes; every other byte comes from disk, so a large config cannot lose settings, gain
    /// invented ones, or have a value flipped on its way through a model. The anchor must match
    /// <b>exactly once</b> — no match, several matches, an empty anchor, or a replacement identical to
    /// the anchor all return a failed <see cref="Result"/> naming the reason, so an approximate edit is
    /// never resolved into a write.
    /// </para>
    /// <para>
    /// <paramref name="copyFromPath"/> seeds the content from another file in the same instance instead
    /// of from the target — for a config that is empty or absent while its defaults sit in a reference
    /// file beside it. The replacement is then applied to that copy; an empty
    /// <paramref name="oldText"/> is allowed in that case alone and copies the reference verbatim.
    /// </para>
    /// <para>
    /// Both paths are path-bound to the instance's own directory exactly like
    /// <see cref="ReadInstanceFileAsync"/>. The source is read under its own size cap, which is larger
    /// than the model-facing read cap on purpose: the bytes go to the confirmation, not into a prompt.
    /// Never throws.
    /// </para>
    /// </summary>
    Task<Result<string>> PrepareInstanceFileEditAsync(
        string instance, string relativePath, string oldText, string newText,
        string? copyFromPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores <paramref name="backupId"/> over the instance's current data. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms — never from the agent
    /// loop. This REPLACES what is there now; the engine owns whatever safety it applies.
    /// </summary>
    Task<Result> RestoreBackupAsync(string instance, string backupId, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes one of the instance's backups. Confirm-path only.</summary>
    Task<Result> DeleteBackupAsync(string instance, string backupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the instance's oldest backups, keeping the <paramref name="keep"/> most recent.
    /// Confirm-path only.
    /// </summary>
    Task<Result> PruneBackupsAsync(string instance, int keep, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects <paramref name="target"/> from the instance. Confirm-path only.
    /// <para>
    /// The <b>form</b> of <paramref name="target"/> — a player name or an account id — is a fact the
    /// game's blueprint declares, and the engine applies it. Nothing above this seam converts between
    /// the two: a converted identifier is one the game will not recognise, and the failure reads like
    /// a permissions problem rather than a wrong-shaped name.
    /// </para>
    /// </summary>
    Task<Result> KickPlayerAsync(string instance, string target, CancellationToken cancellationToken = default);

    /// <summary>Disconnects and blocks <paramref name="target"/>. Confirm-path only. Same target-form
    /// rule as <see cref="KickPlayerAsync"/>.</summary>
    Task<Result> BanPlayerAsync(string instance, string target, CancellationToken cancellationToken = default);

    /// <summary>Lifts a ban on <paramref name="target"/>. Confirm-path only. Same target-form rule as
    /// <see cref="KickPlayerAsync"/>.</summary>
    Task<Result> UnbanPlayerAsync(string instance, string target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets whether the instance starts when the host boots. Confirm-path only.
    /// <para>
    /// This is the supervisor's persisted intent, not a lifecycle action: it changes what happens at
    /// the NEXT boot and does nothing to the running server. That is why it settles against no
    /// run-state postcondition — there is none to observe.
    /// </para>
    /// </summary>
    Task<Result> SetAutostartAsync(string instance, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// One entry in an instance directory listing (<see cref="IServerOperations.ListInstanceDirectoryAsync"/>):
/// a file or subdirectory name, whether it's a directory, and the file size in bytes
/// (0 for directories). Neutral data — the dispatcher formats it for the model.
/// </summary>
public sealed record InstanceDirEntry(string Name, bool IsDirectory, long Size);

/// <summary>
/// What a file search found (<see cref="IServerOperations.FindInstanceFilesAsync"/>).
/// <para>
/// <see cref="Truncated"/> and <see cref="Incomplete"/> stay separate all the way to the model, which
/// is the point of carrying both: "more matched than I showed" invites a narrower pattern, while
/// "I stopped looking" must never be narrated as "there is no such file".
/// </para>
/// </summary>
/// <param name="Paths">Matching paths, relative to the instance's own directory.</param>
/// <param name="Truncated">More files matched than were returned.</param>
/// <param name="Incomplete">The walk stopped on its budget before covering the tree.</param>
public sealed record InstanceFileMatches(
    IReadOnlyList<string> Paths, bool Truncated, bool Incomplete);

/// <summary>One matching line found by a content search: its file, its 1-based line number, and the
/// line itself.</summary>
public sealed record InstanceContentMatch(string Path, int Line, string Text);

/// <summary>
/// What a content search found (<see cref="IServerOperations.SearchInstanceFilesAsync"/>). Carries the
/// same two distinct truncation signals as <see cref="InstanceFileMatches"/>, for the same reason.
/// </summary>
public sealed record InstanceContentMatches(
    IReadOnlyList<InstanceContentMatch> Matches, bool Truncated, bool Incomplete);
