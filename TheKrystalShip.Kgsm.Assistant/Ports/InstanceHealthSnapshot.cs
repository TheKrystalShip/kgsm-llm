namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Root-filesystem disk usage for the host — the one shape both the health check and the
/// <c>host_info</c> read use, so the two can never describe the same disk differently. The surface
/// parses KGSM's <c>df -h</c> strings (e.g. <c>"26%"</c>, <c>"649G"</c>) into this; the aggregator
/// thresholds <see cref="UsedPercent"/>.
/// </summary>
/// <param name="UsedPercent">
/// Disk used percentage as an integer (0–100), parsed from <c>use_percent</c>. Null
/// if the value was present but could not be parsed — the disk check then skips,
/// never assumes a value.
/// </param>
/// <param name="Size">Total size as a human string (e.g. "916G"), for the detail text.</param>
/// <param name="Available">Free space as a human string (e.g. "649G"), for the detail text.</param>
/// <param name="Filesystem">The device backing the mount, when reported.</param>
/// <param name="Used">Space in use as a human string, when reported.</param>
/// <param name="Mount">The mount point, when reported.</param>
public sealed record HostDisk(
    int? UsedPercent,
    string? Size,
    string? Available,
    string? Filesystem = null,
    string? Used = null,
    string? Mount = null);

/// <summary>
/// The raw inputs <see cref="Health.HealthCheckAggregator"/> needs for one instance,
/// in a neutral shape that keeps the assistant library decoupled from kgsm-lib (the
/// host maps its own types onto this — the same boundary <see cref="FleetStatusEntry"/>
/// draws). Surfaces only <b>fetch + map</b>; <b>all judgment lives once in the
/// aggregator</b> (so the error tally can't drift between the Service and bot impls).
/// </summary>
/// <param name="Running">Whether the instance is currently running.</param>
/// <param name="RecentLogLines">
/// Recent log lines (a tail). The aggregator scans these for error severity — it does
/// the tally so the two surface implementations never have to.
/// </param>
/// <param name="RecentLogLinesRequested">
/// How many trailing lines the surface ASKED for when it read <paramref name="RecentLogLines"/>.
/// The aggregator judges whether the sample can support a verdict on this, not on the returned
/// count: a three-line probe cannot say "no errors" however clean it reads, while a 200-line
/// request answered with twelve lines IS the whole log and reading it clean is real evidence.
/// Without it the two cases are indistinguishable, and the aggregator would have to assume the
/// generous one — which is how a keyhole becomes a clean bill of health.
/// </param>
/// <param name="UpdatesAvailable">
/// Whether an update is available: <c>true</c>/<c>false</c> when checked, <c>null</c>
/// when KGSM did not check (honest unknown — the update check then skips).
/// </param>
/// <param name="CurrentVersion">The installed version, if known.</param>
/// <param name="LatestVersion">The latest version, if a check produced one.</param>
/// <param name="HostDisk">
/// Host disk usage, or <c>null</c> if the host read failed (see
/// <paramref name="HostDiskUnavailableReason"/>) — the disk check then skips, never
/// reports a fabricated <c>0%</c>.
/// </param>
/// <param name="HostDiskUnavailableReason">
/// Why host disk usage is absent. Non-null only when <paramref name="HostDisk"/> is null.
/// </param>
/// <param name="PortsReachable">
/// Whether the instance's configured ports are currently bound/reachable: <c>true</c> when all are
/// active, <c>false</c> when the server is running but they aren't, <c>null</c> when not probed
/// (stopped, no ports configured, or the probe failed — the ports check then skips, never fabricating a
/// pass). Reflects HOST-LOCAL port binding (via <c>ss</c>), NOT firewall or router/UPnP reachability.
/// </param>
/// <param name="PortsDetail">
/// A short human detail for the ports probe (e.g. "no ports configured"), or <c>null</c>. Feeds the
/// skip/warn wording so an honest "not checked" reason surfaces rather than a bare skip.
/// </param>
public sealed record InstanceHealthSnapshot(
    bool Running,
    IReadOnlyList<string> RecentLogLines,
    int RecentLogLinesRequested,
    bool? UpdatesAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    HostDisk? HostDisk,
    string? HostDiskUnavailableReason,
    bool? PortsReachable = null,
    string? PortsDetail = null);
