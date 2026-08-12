namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Whether a read reached its authority at all. <see cref="Unavailable"/> means the source could not
/// be consulted (the supervisor is down, the engine refused) — it is never "nothing was found", and a
/// surface renders it as unknown rather than as an empty world.
/// </summary>
public enum FactsState
{
    /// <summary>The authority answered. The payload is a measurement.</summary>
    Available,

    /// <summary>The authority could not be reached. The payload is empty and means nothing.</summary>
    Unavailable,
}

/// <summary>One archived backup of an instance, as its manifest records it.</summary>
/// <param name="Id">The backup's identifier, and what a restore/delete names.</param>
/// <param name="Version">The game version captured, when the manifest records one.</param>
/// <param name="CreatedAt">When the backup was taken, when the manifest records it.</param>
/// <param name="SizeBytes">The archive's size on disk.</param>
/// <param name="Consistency">The manifest's measured consistency verdict, when it carries one.</param>
public sealed record BackupEntry(
    string Id,
    string? Version,
    DateTimeOffset? CreatedAt,
    long SizeBytes,
    string? Consistency);

/// <summary>An instance's backups, most-recent-first.</summary>
public sealed record BackupListing(FactsState State, IReadOnlyList<BackupEntry> Backups);

/// <summary>
/// An instance's installed version against the latest available one.
/// <see cref="UpdateAvailable"/> is null when the comparison could not be made — the engine could not
/// reach the upstream, or the game reports no version — which is not the same as "up to date".
/// <see cref="CheckedAt"/> is when <see cref="Latest"/> was fetched: reporting a version without the
/// moment claims a freshness the reading cannot support.
/// </summary>
public sealed record VersionFacts(
    FactsState State,
    string? Installed,
    string? Latest,
    bool? UpdateAvailable,
    DateTimeOffset? CheckedAt = null);

/// <summary>
/// How an instance's player presence is observed. The supervisor decides this — it depends on whether
/// a blueprint's pattern actually compiles — so no surface may re-derive it from an instance's config.
/// </summary>
public enum PresenceDetection
{
    /// <summary>Matched from the game's own output: real join/leave transitions.</summary>
    Log,

    /// <summary>Polled over RCON and diffed: cannot see churn between polls.</summary>
    Rcon,

    /// <summary>The game reports nothing. Presence is not observable for this instance.</summary>
    None,

    /// <summary>The supervisor could not read the instance, so the capability is unestablished.</summary>
    Unknown,
}

/// <summary>One connected player, as the supervisor's session map holds them.</summary>
public sealed record PlayerEntry(string? Id, string? Name);

/// <summary>
/// One instance's presence. <see cref="Players"/> is readable <b>only</b> through
/// <see cref="IsMeasured"/>: an empty list under <see cref="PresenceDetection.Log"/> or
/// <see cref="PresenceDetection.Rcon"/> means nobody is connected, and an empty list under
/// <see cref="PresenceDetection.None"/> or <see cref="PresenceDetection.Unknown"/> means nobody can
/// tell. Rendering the second as "0 online" states something the host does not know, so the pair
/// travels together and is never split.
/// </summary>
public sealed record InstancePresence(
    string Instance,
    PresenceDetection Detection,
    IReadOnlyList<PlayerEntry> Players)
{
    /// <summary>Whether this instance's roster is a measurement rather than an absence of one.</summary>
    public bool IsMeasured => Detection is PresenceDetection.Log or PresenceDetection.Rcon;
}

/// <summary>
/// Presence for every instance the supervisor knows, which is the point: a bare session list leaves an
/// absent instance ambiguous between "nobody is online" and "this game cannot report players".
/// <para>
/// <see cref="FactsState.Unavailable"/> here is a third, distinct outcome from either detection value —
/// the supervisor itself could not be reached, so nothing is known about any instance.
/// </para>
/// </summary>
public sealed record PresenceReading(FactsState State, IReadOnlyList<InstancePresence> Instances);

/// <summary>The instances set to start at boot.</summary>
public sealed record AutostartReading(FactsState State, IReadOnlyList<string> EnabledInstances);

/// <summary>
/// The tail of an instance's captured console output. Distinct from reading a log file: a game that
/// writes no log still has console output, and this is the only place it exists.
/// </summary>
public sealed record ConsoleTail(FactsState State, IReadOnlyList<string> Lines);

/// <summary>
/// One run of an instance's console — a single stretch of output between a start and the exit after
/// it. The supervisor rotates the log on every fresh start, so a server that crashed and was
/// restarted has its cause in one run and a clean boot in the next.
/// </summary>
/// <param name="Index">Newest-first position; 0 is the most recent run.</param>
/// <param name="Current">Whether the run is still in progress.</param>
/// <param name="EndedAt">
/// When the run stopped printing, or null while it is <paramref name="Current"/>. This is what a
/// crash is matched against when <paramref name="Outcome"/> cannot name the run outright.
/// </param>
/// <param name="Outcome">
/// How the supervisor classified this run's ending: <c>crashed</c>, <c>gave-up</c>, <c>exited</c>,
/// <c>stopped</c>, <c>running</c>, or <c>unknown</c>.
/// <para>
/// <b><c>unknown</c> means the ending was never recorded</b> — not that the run ended cleanly. A run
/// that predates the supervisor's ledger, or that ended while the daemon was down, reports it.
/// </para>
/// </param>
/// <param name="ExitCode">
/// The exit code the supervisor read, where it could. Null is an honest unknown. Never a verdict on
/// its own: game servers exit 0 on a fatal error often enough that the code alone proves nothing.
/// </param>
public sealed record ConsoleRunInfo(
    int Index,
    bool Current,
    DateTimeOffset? EndedAt,
    string Outcome = ConsoleRunInfo.UnknownOutcome,
    int? ExitCode = null)
{
    /// <summary>The supervisor saw this run exit while it was wanted running.</summary>
    public const string CrashedOutcome = "crashed";

    /// <summary>It crashed, and the supervisor stopped trying to bring it back.</summary>
    public const string GaveUpOutcome = "gave-up";

    /// <summary>Nothing recorded how this run ended. An absence of knowledge, never a clean ending.</summary>
    public const string UnknownOutcome = "unknown";

    /// <summary>Whether the supervisor itself saw this run fail, however the retries then went.</summary>
    public bool Crashed => Outcome is CrashedOutcome or GaveUpOutcome;
}

/// <summary>The runs of an instance's console, newest first.</summary>
public sealed record ConsoleRuns(FactsState State, IReadOnlyList<ConsoleRunInfo> Runs);

/// <summary>
/// Per-instance facts the assistant reads but never changes — the read half of the engine seam, beside
/// <see cref="IServerOperations"/>'s mutations. Reads and mutations are separated here for the same
/// reason they are separated in the tool catalog: authorization is decided before a call is offered,
/// so a seam that mixes the two cannot be offered honestly.
/// <para>
/// Every method reports an unreachable authority as <see cref="FactsState.Unavailable"/> rather than
/// as an empty result, and implementations MUST NOT throw.
/// </para>
/// </summary>
public interface IServerFacts
{
    /// <summary>Lists an instance's backups, most-recent-first.</summary>
    Task<BackupListing> GetBackupsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Reads an instance's installed and latest versions.</summary>
    Task<VersionFacts> GetVersionAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads presence for every instance in one call. Whole-host by design — the supervisor answers
    /// for all of them at once, and asking per instance would be N round-trips for the same map.
    /// </summary>
    Task<PresenceReading> GetPresenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the set of instances enabled to start at boot.</summary>
    Task<AutostartReading> GetAutostartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the last <paramref name="lines"/> lines of an instance's MOST RECENT run of console
    /// output. After a crash-restart that is the run that came after the crash — see
    /// <see cref="GetConsoleRunsAsync"/> for reaching the one that holds it.
    /// </summary>
    Task<ConsoleTail> GetConsoleTailAsync(string instance, int lines, CancellationToken cancellationToken = default);

    /// <summary>Lists the runs of an instance's console, newest first.</summary>
    Task<ConsoleRuns> GetConsoleRunsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the last <paramref name="lines"/> lines of ONE run, addressed by its
    /// <see cref="ConsoleRunInfo.Index"/> in the listing it came from.
    /// </summary>
    Task<ConsoleTail> GetConsoleRunTailAsync(
        string instance, int lines, int run, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IServerFacts"/> for a host that has wired no engine: every read fails closed as
/// <see cref="FactsState.Unavailable"/>, so embedding the assistant library never breaks DI.
/// <c>AddKgsmAssistant</c> registers this with <c>TryAddSingleton</c> and <c>AddKgsmAdapters</c>
/// registers the real adapter afterward, which is the one resolved.
/// </summary>
public sealed class UnavailableServerFacts : IServerFacts
{
    public Task<BackupListing> GetBackupsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BackupListing(FactsState.Unavailable, []));

    public Task<VersionFacts> GetVersionAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VersionFacts(FactsState.Unavailable, null, null, null));

    public Task<PresenceReading> GetPresenceAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PresenceReading(FactsState.Unavailable, []));

    public Task<AutostartReading> GetAutostartAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AutostartReading(FactsState.Unavailable, []));

    public Task<ConsoleTail> GetConsoleTailAsync(string instance, int lines, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleTail(FactsState.Unavailable, []));

    public Task<ConsoleRuns> GetConsoleRunsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleRuns(FactsState.Unavailable, []));

    public Task<ConsoleTail> GetConsoleRunTailAsync(
        string instance, int lines, int run, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleTail(FactsState.Unavailable, []));
}
