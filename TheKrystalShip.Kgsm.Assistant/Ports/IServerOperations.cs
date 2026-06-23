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
    Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default);

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
}

/// <summary>
/// One entry in an instance directory listing (<see cref="IServerOperations.ListInstanceDirectoryAsync"/>):
/// a file or subdirectory name, whether it's a directory, and the file size in bytes
/// (0 for directories). Neutral data — the dispatcher formats it for the model.
/// </summary>
public sealed record InstanceDirEntry(string Name, bool IsDirectory, long Size);
