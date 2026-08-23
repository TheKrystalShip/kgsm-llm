using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Files;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IServerOperations"/> port by calling KGSM.Lib's
/// <see cref="IInstanceService"/> and <see cref="IInstanceFiles"/> directly. We depend on
/// these individual services (which shell out to kgsm via a process runner / do direct
/// jailed file I/O) rather than the full <c>IKgsmClient</c> ON PURPOSE: constructing
/// <c>IKgsmClient</c> auto-starts KGSM.Lib's Unix-socket event listener, which would contend
/// with the Discord bot for the single kgsm event socket. This service ingests events over
/// the HTTP webhook instead, so it must never bind that socket. (<see cref="IInstanceFiles"/>
/// itself only injects <see cref="IInstanceService"/>, so it's socket-safe too.)
/// <para>
/// The KGSM.Lib instance/file services are synchronous, so calls are offloaded with
/// <see cref="Task.Run(Action)"/>. Per the port contract these never throw — failures map
/// to a failed <see cref="Result"/>.
/// </para>
/// </summary>
internal sealed class KgsmServerOperations : IServerOperations
{
    private readonly IInstanceService _instances;
    private readonly IInstanceFiles _files;
    private readonly ISystemService _system;
    private readonly IWatcherService _watcher;
    private readonly IWatchdogClient _watchdog;
    private readonly IInvocationContext _invocation;
    private readonly IToolCatalog _catalog;
    private readonly ILogger<KgsmServerOperations> _logger;

    /// <summary>
    /// What a capability is CALLED on this deploy. These refusals are read by the model and tell it
    /// which tool to reach for next, so the name has to be the one the catalog is actually offering —
    /// a literal would keep pointing at the old name after a rename.
    /// </summary>
    private string Name(Capability capability) => _catalog.NameOf(capability).Name;

    // kgsm's `watcher ports test` exit code when the instance has no ports configured — treated as
    // "not applicable" (the health ports check skips) rather than "not reachable".
    private const int NoPortsConfiguredExit = 44; // EC_WATCHER_PORT_NOT_ACTIVE

    public KgsmServerOperations(
        IInstanceService instances, IInstanceFiles files, ISystemService system, IWatcherService watcher,
        IWatchdogClient watchdog, IInvocationContext invocation, IToolCatalog catalog,
        ILogger<KgsmServerOperations> logger)
    {
        _instances = instances;
        _files = files;
        _system = system;
        _watcher = watcher;
        _watchdog = watchdog;
        _invocation = invocation;
        _catalog = catalog;
        _logger = logger;
    }

    // The provenance of the action being performed (set at the HTTP entry point), or (null, null) for a
    // non-attributed path — KGSM then applies its honest fallback, never a fabricated actor.
    private (string? Actor, string? Origin) Provenance()
    {
        Invocation? inv = _invocation.Current;
        return (inv?.Actor, inv?.Origin);
    }

    public Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(StartAsync), instance, () => _instances.Start(instance, actor, origin), cancellationToken);
    }

    public Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(StopAsync), instance, () => _instances.Stop(instance, actor, origin), cancellationToken);
    }

    public Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(RestartAsync), instance, () => _instances.Restart(instance, actor, origin), cancellationToken);
    }

    public Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(CreateBackupAsync), instance, () => _instances.CreateBackup(instance, actor, origin), cancellationToken);
    }

    public Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(UpdateAsync), instance, () => _instances.Update(instance, actor, origin), cancellationToken);
    }

    public async Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Task.Run(() => _instances.GetStatus(instance), cancellationToken);
            return result.IsSuccess
                ? Result.Success(result.Stdout ?? string.Empty)
                : Result.Failure<string>(result.Stderr ?? "unknown error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStatus failed for {Instance}", instance);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>
    /// Reads the instance's structured info (kgsm's <c>instances info --json</c>) and renders its
    /// <see cref="Instance.Ports"/> (the canonical <see cref="PortMapping"/> list) back to a UFW-style
    /// spec string via <see cref="PortMappingExtensions.ToUfwSpec"/>. The assistant's <c>open_ports</c>
    /// tool consumes this so the model never supplies or guesses ports — the configured ports are the
    /// engine's truth. An unknown instance or one with no ports configured maps to a failed Result
    /// (honest reason, never fabricated), never throws.
    /// </summary>
    public async Task<Result<string>> GetConfiguredPortsAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await Task.Run(() => _instances.GetInstanceInfo(instance), cancellationToken);
            if (info is null)
                return Result.Failure<string>($"'{instance}' did not return instance info (it may not be a known instance).");
            var ports = info.Ports;
            if (ports is null || ports.Count == 0)
                return Result.Failure<string>($"'{instance}' has no ports configured in its blueprint — nothing to open.");
            var spec = ports.ToUfwSpec();
            if (string.IsNullOrWhiteSpace(spec))
                return Result.Failure<string>($"'{instance}' has no ports configured in its blueprint — nothing to open.");
            return Result.Success(spec);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetConfiguredPorts failed for {Instance}", instance);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>Cap on bytes read from a config file — bounds what flows into the model's context.</summary>
    private const int MaxFileBytes = 64 * 1024;

    /// <summary>
    /// Reads a file inside the instance's jail via <see cref="IInstanceFiles"/> — the single
    /// jailed filesystem authority (kgsm-lib), rooted at the instance's <c>working_dir</c>.
    /// A blank <paramref name="relativePath"/> defaults to the instance's own
    /// <c>&lt;name&gt;.config.ini</c> (same convention as before, now working-dir-relative).
    /// </summary>
    public async Task<Result<string>> ReadInstanceFileAsync(
        string instance, string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var relPath = string.IsNullOrWhiteSpace(relativePath) ? $"{instance}.config.ini" : relativePath;

            var result = await Task.Run(() => _files.Read(instance, relPath, MaxFileBytes), cancellationToken);
            return result.Outcome switch
            {
                FileOpOutcome.Ok => Result.Success(result.Value!.Content),
                // Honest placeholder, not a failure — mirrors the prior behaviour of reporting
                // that the file exists and is binary rather than refusing the read outright.
                FileOpOutcome.Binary => Result.Success("[binary file — not shown]"),
                FileOpOutcome.NotFound => Result.Failure<string>("The requested file was not found."),
                FileOpOutcome.OutOfJail => Result.Failure<string>(
                    "Refused: the requested file is outside the instance directory."),
                FileOpOutcome.NotAFile => Result.Failure<string>(
                    "Refused: that path isn't a regular file (it may be a directory, socket, pipe, or device)."),
                FileOpOutcome.TooLarge => Result.Failure<string>(
                    $"Refused: the file is over the {MaxFileBytes / 1024} KB read limit."),
                FileOpOutcome.InstanceUnavailable => Result.Failure<string>($"'{instance}' is not a known instance."),
                _ => Result.Failure<string>(result.Message ?? "could not read the file."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadInstanceFile failed for {Instance} ({Path})", instance, relativePath);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>Max entries returned in one directory listing — bounds the model-facing text.</summary>
    private const int MaxListEntries = 200;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InstanceDirEntry>>> ListInstanceDirectoryAsync(
        string instance, string? relativeSubdir = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Task.Run(
                () => _files.List(instance, relativeSubdir, MaxListEntries), cancellationToken);
            return result.Outcome switch
            {
                FileOpOutcome.Ok => Result.Success<IReadOnlyList<InstanceDirEntry>>(
                    result.Value!.Entries.Select(MapDirEntry).ToList()),
                FileOpOutcome.NotFound or FileOpOutcome.NotADirectory => Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    "That isn't a directory in the server's folder (omit the subdirectory to list the top level)."),
                FileOpOutcome.OutOfJail => Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    "Refused: that path is outside the instance directory."),
                FileOpOutcome.InstanceUnavailable => Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    $"'{instance}' is not a known instance."),
                _ => Result.Failure<IReadOnlyList<InstanceDirEntry>>(result.Message ?? "could not list the directory."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListInstanceDirectory failed for {Instance} ({Subdir})", instance, relativeSubdir);
            return Result.Failure<IReadOnlyList<InstanceDirEntry>>(ex.Message);
        }
    }

    /// <summary>How many matches a single find returns before it reports truncation.</summary>
    private const int MaxFindMatches = 60;

    public async Task<Result<InstanceFileMatches>> FindInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Task.Run(
                () => _files.Find(instance, pattern, relativeSubdir, new FindOptions(MaxResults: MaxFindMatches)),
                cancellationToken);

            return result.Outcome switch
            {
                FileOpOutcome.Ok => Result.Success(new InstanceFileMatches(
                    result.Value!.Matches.Select(m => m.Path).ToList(),
                    result.Value!.Truncated,
                    result.Value!.ScanLimitHit)),
                FileOpOutcome.InvalidArgument => Result.Failure<InstanceFileMatches>(
                    "That isn't a usable search pattern."),
                FileOpOutcome.NotFound or FileOpOutcome.NotADirectory => Result.Failure<InstanceFileMatches>(
                    "That isn't a directory in the server's folder (omit the subdirectory to search from the top)."),
                FileOpOutcome.OutOfJail => Result.Failure<InstanceFileMatches>(
                    "Refused: that path is outside the instance directory."),
                FileOpOutcome.InstanceUnavailable => Result.Failure<InstanceFileMatches>(
                    $"'{instance}' is not a known instance."),
                _ => Result.Failure<InstanceFileMatches>(result.Message ?? "could not search the directory."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindInstanceFiles failed for {Instance} ({Pattern})", instance, pattern);
            return Result.Failure<InstanceFileMatches>(ex.Message);
        }
    }

    /// <summary>How many matching lines a single content search returns before reporting truncation.</summary>
    private const int MaxSearchHits = 40;

    public async Task<Result<InstanceContentMatches>> SearchInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null, bool ignoreCase = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Task.Run(
                () => _files.Search(
                    instance, pattern, relativeSubdir,
                    new FileSearchOptions(MaxHits: MaxSearchHits, IgnoreCase: ignoreCase)),
                cancellationToken);

            return result.Outcome switch
            {
                FileOpOutcome.Ok => Result.Success(new InstanceContentMatches(
                    result.Value!.Hits.Select(h => new InstanceContentMatch(h.Path, h.LineNumber, h.Line)).ToList(),
                    result.Value!.Truncated,
                    result.Value!.ScanLimitHit)),
                FileOpOutcome.InvalidArgument => Result.Failure<InstanceContentMatches>(
                    "That isn't a valid search expression."),
                FileOpOutcome.NotFound or FileOpOutcome.NotADirectory => Result.Failure<InstanceContentMatches>(
                    "That isn't a directory in the server's folder (omit the subdirectory to search from the top)."),
                FileOpOutcome.OutOfJail => Result.Failure<InstanceContentMatches>(
                    "Refused: that path is outside the instance directory."),
                FileOpOutcome.InstanceUnavailable => Result.Failure<InstanceContentMatches>(
                    $"'{instance}' is not a known instance."),
                _ => Result.Failure<InstanceContentMatches>(result.Message ?? "could not search the files."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchInstanceFiles failed for {Instance} ({Pattern})", instance, pattern);
            return Result.Failure<InstanceContentMatches>(ex.Message);
        }
    }

    private static InstanceDirEntry MapDirEntry(FileEntry entry) =>
        new(entry.Name, entry.Kind == FileKind.Dir, entry.SizeBytes ?? 0, entry.Mtime);

    public async Task<Result<IReadOnlyList<FleetStatusEntry>>> GetFleetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // fast: true skips the per-instance network update-check (~20x cheaper);
            // a fleet liveness read has no business polling for updates.
            var statuses = await Task.Run(() => _instances.GetAllStatuses(fast: true), cancellationToken);

            var entries = statuses
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => MapFleetEntry(kv.Key, kv.Value))
                .ToList();

            return Result.Success<IReadOnlyList<FleetStatusEntry>>(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFleetStatus failed");
            return Result.Failure<IReadOnlyList<FleetStatusEntry>>(ex.Message);
        }
    }

    /// <summary>
    /// Maps a kgsm-lib <see cref="Reading{T}"/> onto the toolbox-boundary
    /// <see cref="FleetStatusEntry"/>, preserving the measured-vs-unavailable
    /// distinction. A non-measured reading becomes <see cref="FleetStatusAvailability.Unavailable"/>
    /// with <c>Running = null</c> — never a fabricated "stopped."
    /// </summary>
    private static FleetStatusEntry MapFleetEntry(string name, Reading<InstanceRuntimeStatus> reading) =>
        reading.State == ReadingState.Measured
            ? new FleetStatusEntry(name, FleetStatusAvailability.Read, reading.Value!.Status, Reason: null)
            : new FleetStatusEntry(
                name,
                FleetStatusAvailability.Unavailable,
                Running: null,
                Reason: reading.Reason ?? DescribeReadingCode(reading.Code));

    private static string DescribeReadingCode(ReadingCode? code) => code switch
    {
        ReadingCode.RequiresRegeneration => "its management file must be regenerated to report status",
        ReadingCode.DeadlineExceeded => "the status read timed out",
        ReadingCode.MonitorOffline => "the status source is offline",
        ReadingCode.SourceError => "the status source returned an error",
        _ => "the status could not be read",
    };

    public async Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var active = await Task.Run(() => _instances.IsActive(instance), cancellationToken);
            return Result.Success(active);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsActive failed for {Instance}", instance);
            return Result.Failure<bool>(ex.Message);
        }
    }

    /// <summary>
    /// Fetches the neutral health inputs for one instance (fetch + map only — the
    /// health judgment lives in <c>HealthCheckAggregator</c>). One non-fast status read
    /// supplies running-state and the real update check; the log sample comes from the
    /// supervisor's console and host disk from <c>system info</c>. A failed host read maps to a
    /// null disk + reason (the aggregator then skips the disk check) — never a fabricated
    /// <c>0%</c>.
    /// </summary>
    public async Task<Result<InstanceHealthSnapshot>> GetHealthSnapshotAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            // Non-fast: this performs the per-instance update check, so UpdatesAvailable
            // is a real tri-state (true/false/null), not skipped like the fleet read.
            var status = await Task.Run(() => _instances.GetInstanceStatus(instance), cancellationToken);
            if (status is null)
                return Result.Failure<InstanceHealthSnapshot>(
                    $"'{instance}' did not return a status (it may need its management file regenerated).");

            var (logLines, logLinesRequested) = await ReadHealthLogSampleAsync(
                instance, status.RecentLogs, cancellationToken);

            // Host disk is best-effort: its absence skips the disk check, it never fails the read.
            HostDisk? hostDisk = null;
            string? diskReason = null;
            try
            {
                var info = await Task.Run(() => _system.GetSystemInfo(), cancellationToken);
                if (info is null)
                    diskReason = "host system info was unavailable";
                else
                    hostDisk = new HostDisk(
                        ParsePercent(info.Disk.UsePercent), info.Disk.Size, info.Disk.Available);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host disk read failed for health check of {Instance}", instance);
                diskReason = "the host disk usage could not be read";
            }

            // Port reachability is best-effort and only meaningful while running: its absence skips the
            // ports check, it never fails the read. `watcher ports test` probes whether the configured
            // ports are currently bound (host-local `ss`), NOT firewall/router reachability.
            bool? portsReachable = null;
            string? portsDetail = null;
            if (status.Status)
                (portsReachable, portsDetail) = await Task.Run(
                    () => ProbePorts(instance), cancellationToken);

            var snapshot = new InstanceHealthSnapshot(
                Running: status.Status,
                RecentLogLines: logLines,
                RecentLogLinesRequested: logLinesRequested,
                UpdatesAvailable: status.Version.UpdatesAvailable,
                CurrentVersion: NullIfEmpty(status.Version.Current),
                LatestVersion: status.Version.Latest,
                HostDisk: hostDisk,
                HostDiskUnavailableReason: diskReason,
                PortsReachable: portsReachable,
                PortsDetail: portsDetail,
                Restart: status.Status ? await ReadRestartAsync(instance, cancellationToken) : null);

            return Result.Success(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHealthSnapshot failed for {Instance}", instance);
            return Result.Failure<InstanceHealthSnapshot>(ex.Message);
        }
    }

    /// <summary>
    /// Best-effort port-reachability probe for the health check, mapping kgsm's <c>watcher ports test</c>
    /// exit code to the neutral (reachable?, detail) shape. Exit 0 → all configured ports active; the
    /// no-ports-configured code → not applicable (null → the check skips); any other non-zero while
    /// running → not active (a warning); a thrown probe → null (skip), never a fabricated pass.
    /// </summary>
    private (bool?, string?) ProbePorts(string instance)
    {
        try
        {
            var result = _watcher.TestPortWatch(instance);
            if (result.ExitCode == 0)
                return (true, null);
            if (result.ExitCode == NoPortsConfiguredExit)
                return (null, "no ports configured");
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Port reachability probe failed for health check of {Instance}", instance);
            return (null, null);
        }
    }

    /// <summary>Splits KGSM's newline-joined <c>recent_logs</c> tail into non-empty lines.</summary>
    /// <summary>How many console lines the health scan asks for — enough to be evidence.</summary>
    private const int HealthLogSampleLines = 200;

    /// <summary>
    /// Reads the log sample the health verdict is judged on, and reports how many lines were ASKED
    /// for so the aggregator can tell an adequate scan from a keyhole.
    /// <para>
    /// The supervisor's console is the source. KGSM's status carries a three-line tail — sized to
    /// display, not to conclude from — so a verdict resting on it says "no errors" from a sample
    /// that could not have held one. A container's stdout belongs to Docker and an unreachable
    /// supervisor answers nothing, so both fall back to that status tail with its real (tiny) size
    /// attached, and the aggregator honestly skips instead of reading a clean bill out of it.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Lines, int Requested)> ReadHealthLogSampleAsync(
        string instance, string? statusRecentLogs, CancellationToken cancellationToken)
    {
        var fallback = SplitLogLines(statusRecentLogs);
        try
        {
            var console = await _watchdog.GetConsoleTailAsync(
                instance, HealthLogSampleLines, cancellationToken);
            if (console.Count > 0)
                return (console, HealthLogSampleLines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Console read for the health check of {Instance} failed", instance);
        }

        return (fallback, fallback.Count);
    }

    /// <summary>
    /// When this run began and how the one before it ended, from the supervisor's run list, or null
    /// when there is nothing to say: only one run on record, or a list that could not be read.
    /// <para>
    /// A run's start is not recorded anywhere, so it is taken from the previous run's ending — the
    /// spawn follows it within about a second. That is measured, unlike a start time, which is why
    /// the absence of a previous run yields null rather than a guess at how long this one has been up.
    /// </para>
    /// <para>
    /// Best-effort, exactly like the log sample and the disk read beside it: a failure here skips the
    /// stability check rather than failing the health read.
    /// </para>
    /// </summary>
    private async Task<InstanceRestart?> ReadRestartAsync(string instance, CancellationToken cancellationToken)
    {
        try
        {
            var runs = await _watchdog.GetConsoleRunsAsync(instance, cancellationToken);

            // The run in progress is index 0; the one before it is what ended to make room for it.
            // Anything else means the supervisor is describing a shape this cannot read, so say nothing.
            if (runs.Count < 2 || !runs[0].Current || runs[1].EndedAt is not { } endedAt)
                return null;

            // Only a run the supervisor says FAILED is worth reading — a deliberate stop has no last
            // words to report, and fetching every previous run's tail on every health check would be
            // a socket read spent on output nothing will quote. Gated on a measured classification
            // rather than on whether the crash is recent: how recent is the aggregator's judgment to
            // make, and duplicating it here is how two answers to one question start to disagree.
            IReadOnlyList<string>? previousLines = null;
            if (runs[1].Outcome is "crashed" or "gave-up")
                previousLines = await ReadRunTailAsync(instance, run: 1, cancellationToken);

            _logger.LogDebug(
                "{Instance}'s previous run ended {EndedAt:o} as {Outcome} (exit {Exit}); read {Lines} line(s) of it",
                instance, endedAt, runs[1].Outcome, runs[1].ExitCode, previousLines?.Count ?? 0);

            return new InstanceRestart(
                new DateTimeOffset(endedAt, TimeSpan.Zero),
                runs[1].Outcome,
                runs[1].ExitCode,
                previousLines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run list read for the health check of {Instance} failed", instance);
            return null;
        }
    }

    /// <summary>
    /// The tail of one earlier run, or null when it could not be read. Deep enough that a fatal line
    /// is still in view behind the stack frames a runtime prints under it, and no deeper — the
    /// aggregator quotes at most one line of what comes back.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReadRunTailAsync(
        string instance, int run, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await _watchdog.GetConsoleRunTailAsync(
                instance, CrashedRunSampleLines, run, cancellationToken);
            return lines.Count > 0 ? lines : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Crashed-run console read for {Instance} failed", instance);
            return null;
        }
    }

    /// <summary>How much of a crashed run's tail the health check reads to find its last words.</summary>
    private const int CrashedRunSampleLines = 60;

    private static IReadOnlyList<string> SplitLogLines(string? recentLogs) =>
        string.IsNullOrEmpty(recentLogs)
            ? Array.Empty<string>()
            : recentLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parses the leading integer of a <c>df</c> use-percent string like <c>"26%"</c>.
    /// Returns null if it can't be parsed (the disk check then skips, never assumes 0).
    /// </summary>
    private static int? ParsePercent(string? usePercent)
    {
        if (string.IsNullOrWhiteSpace(usePercent))
            return null;
        var digits = new string(usePercent.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var pct) ? pct : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<Result> InstallAsync(
        string blueprint,
        string? instanceName,
        CancellationToken cancellationToken = default,
        string? version = null,
        int? port = null,
        string? library = null)
    {
        try
        {
            // Long-running; completion is also broadcast via events. Mirror the bot: run it
            // and report queued-successfully unless it throws synchronously.
            var (actor, origin) = Provenance();
            await Task.Run(
                () => _instances.Install(
                    blueprint, library: library, version: version, name: instanceName,
                    actor: actor, origin: origin, port: port),
                cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install failed for blueprint {Blueprint} (name={Name})", blueprint, instanceName);
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var (actor, origin) = Provenance();
            await Task.Run(() => _instances.Uninstall(instance, actor, origin), cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uninstall failed for {Instance}", instance);
            return Result.Failure(ex.Message);
        }
    }

    public Task<Result> SetInstanceConfigValueAsync(
        string instance, string key, string value, CancellationToken cancellationToken = default)
    {
        // RunAsync surfaces kgsm's stderr on a non-zero exit, so a denylisted/invalid key
        // (kgsm owns that policy) reaches the user as the failed Result's message.
        var (actor, origin) = Provenance();
        return RunAsync(nameof(SetInstanceConfigValueAsync), instance,
            () => _instances.SetInstanceConfigValue(instance, key, value, actor, origin), cancellationToken);
    }

    public Task<Result> RestoreBackupAsync(
        string instance, string backupId, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(RestoreBackupAsync), instance,
            () => _instances.RestoreBackup(instance, backupId, actor, origin), cancellationToken);
    }

    public Task<Result> DeleteBackupAsync(
        string instance, string backupId, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(DeleteBackupAsync), instance,
            () => _instances.DeleteBackup(instance, backupId, actor, origin), cancellationToken);
    }

    public Task<Result> PruneBackupsAsync(
        string instance, int keep, CancellationToken cancellationToken = default)
    {
        // kgsm-lib throws below 1 rather than treating it as "keep nothing"; refuse it here so the
        // confirm path reports a reason instead of an exception.
        if (keep < 1)
            return Task.FromResult(Result.Failure("A prune must keep at least one backup."));

        var (actor, origin) = Provenance();
        return RunAsync(nameof(PruneBackupsAsync), instance,
            () => _instances.PruneBackups(instance, keep, actor, origin), cancellationToken);
    }

    public Task<Result> KickPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(KickPlayerAsync), instance,
            () => _instances.Kick(instance, target, actor, origin), cancellationToken);
    }

    public Task<Result> BanPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(BanPlayerAsync), instance,
            () => _instances.Ban(instance, target, actor, origin), cancellationToken);
    }

    public Task<Result> UnbanPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default)
    {
        var (actor, origin) = Provenance();
        return RunAsync(nameof(UnbanPlayerAsync), instance,
            () => _instances.Unban(instance, target, actor, origin), cancellationToken);
    }

    /// <summary>
    /// Sets the supervisor's persisted boot-autostart intent. Goes to the watchdog rather than the
    /// engine because the watchdog is what owns that set and acts on it at boot.
    /// </summary>
    public async Task<Result> SetAutostartAsync(
        string instance, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = enabled
                ? await _watchdog.EnableAsync(instance, cancellationToken)
                : await _watchdog.DisableAsync(instance, cancellationToken);

            return result.Ok
                ? Result.Success()
                : Result.Failure(NullIfEmpty(result.Message) ?? "the supervisor refused the change");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autostart change failed for {Instance} (enabled={Enabled})", instance, enabled);
            return Result.Failure($"the supervisor could not be reached ({ex.Message})");
        }
    }

    /// <summary>Generous size cap for a whole-file overwrite — headroom for future non-config
    /// uses, not a target.</summary>
    private const int MaxWriteBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Writes (creating if absent) a file inside the instance's jail via <see cref="IInstanceFiles"/>.
    /// <c>AllowCreate</c>/<c>Backup</c> stay on — the assistant's write always may create a new file
    /// inside an existing directory and always keeps a last-good <c>.kgsmbak</c> sibling before an
    /// overwrite (kgsm-lib's atomic temp-file-then-rename makes the replace itself crash-safe).
    /// Last-writer-wins (<c>ExpectedEtag = null</c>) — the assistant doesn't round-trip an etag today.
    /// </summary>
    public async Task<Result> WriteInstanceFileAsync(
        string instance, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            var opts = new WriteOptions
            {
                AllowCreate = true,
                Backup = true,
                MaxBytes = MaxWriteBytes,
                ExpectedEtag = null,
            };

            var result = await Task.Run(() => _files.Write(instance, relativePath, content, opts), cancellationToken);
            return result.Outcome switch
            {
                FileOpOutcome.Ok => Result.Success(),
                FileOpOutcome.NotFound => Result.Failure(
                    "Refused: the containing directory doesn't exist — only a file inside an existing " +
                    "server directory can be created."),
                FileOpOutcome.NotADirectory => Result.Failure(
                    "Refused: the target's containing path isn't a directory."),
                FileOpOutcome.OutOfJail => Result.Failure("Refused: the target file is outside the instance directory."),
                FileOpOutcome.NotAFile => Result.Failure(
                    "Refused: that path isn't a regular file (it may be a directory, socket, pipe, or device)."),
                FileOpOutcome.Binary => Result.Failure(
                    "Refused: the existing file isn't text — overwriting it as text is refused."),
                FileOpOutcome.TooLarge => Result.Failure(
                    $"Refused: the content is over the {MaxWriteBytes / (1024 * 1024)} MB limit."),
                FileOpOutcome.InstanceUnavailable => Result.Failure($"'{instance}' is not a known instance."),
                _ => Result.Failure(result.Message ?? "could not write the file."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteInstanceFile failed for {Instance} ({Path})", instance, relativePath);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Cap on the bytes read to resolve an edit. Larger than the model-facing read cap because these
    /// bytes never enter a prompt — they are the file's own content, carried to the confirmation.
    /// </summary>
    private const int MaxEditSourceBytes = 1024 * 1024;

    /// <summary>
    /// Reads the file the edit applies to (the target, or <paramref name="copyFromPath"/> when the
    /// content is seeded from a reference file) through the same jail as every other read, then applies
    /// the single anchored replacement with <see cref="FileEdit"/>. Every refusal — a missing file, an
    /// anchor that matches nowhere or in several places, a binary or oversized source — comes back as a
    /// failed <see cref="Result"/> whose message tells the caller what to do instead, because the one
    /// thing this must never do is resolve into an approximate write.
    /// </summary>
    public async Task<Result<string>> PrepareInstanceFileEditAsync(
        string instance, string relativePath, string oldText, string newText,
        string? copyFromPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var seeded = !string.IsNullOrWhiteSpace(copyFromPath);
            var sourcePath = seeded ? copyFromPath!.Trim() : relativePath;

            if (seeded)
            {
                var reachable = await SeededTargetIsReachableAsync(instance, relativePath, cancellationToken);
                if (!reachable.IsSuccess)
                    return Result.Failure<string>(reachable.Error!);
            }

            var source = await ReadEditSourceAsync(instance, sourcePath, seeded, cancellationToken);
            if (!source.IsSuccess)
                return Result.Failure<string>(source.Error!);

            // An empty anchor is a copy, and only a seeded write can mean one: there is no text to
            // replace, so the reference file's content IS the proposal.
            if (oldText.Length == 0)
                return seeded
                    ? Result.Success(source.Value!)
                    : Result.Failure<string>(
                        $"no text to replace was given. Pass old_string exactly as {Name(LlmTools.ReadFile)} showed it.");

            var edit = FileEdit.Apply(source.Value!, oldText, newText);
            return edit.Outcome switch
            {
                FileEditOutcome.Applied => Result.Success(edit.Content!),
                FileEditOutcome.NoMatch when source.Value!.Trim().Length == 0 =>
                    Result.Failure<string>(
                        $"'{sourcePath}' is empty, so there is nothing to replace in it. Pass the game's "
                        + "default/reference file as copy_from to fill it in."),
                FileEditOutcome.NoMatch => Result.Failure<string>(
                    $"the text to replace is not in '{sourcePath}'. Read the file again and copy the line "
                    + "exactly as it appears — spacing, case and punctuation included — rather than "
                    + "retyping it."),
                FileEditOutcome.Ambiguous => Result.Failure<string>(
                    $"the text to replace appears in more than one place in '{sourcePath}', so which one "
                    + "was meant is unknown. Include enough surrounding text for it to match exactly one "
                    + "place."),
                FileEditOutcome.NoChange => Result.Failure<string>(
                    "the replacement is identical to the text it replaces, so this edit changes nothing."),
                _ => Result.Failure<string>(
                    $"no text to replace was given. Pass old_string exactly as {Name(LlmTools.ReadFile)} showed it."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PrepareInstanceFileEdit failed for {Instance} ({Path})", instance, relativePath);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>
    /// Reads the same source the anchored edit reads, through the same jail and the same size cap, then
    /// replaces one <c>Key=Value</c> setting with <see cref="SettingEdit"/>. Sharing the read is what
    /// keeps the two ways of naming a change honest about the same file: a path that is outside the
    /// instance, binary, absent or oversized fails identically whichever way the caller addressed the
    /// setting.
    /// </summary>
    public async Task<Result<SettingEditSummary>> PrepareInstanceSettingEditAsync(
        string instance, string relativePath, string settingKey, string settingValue,
        string? copyFromPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var seeded = !string.IsNullOrWhiteSpace(copyFromPath);
            var sourcePath = seeded ? copyFromPath!.Trim() : relativePath;

            if (seeded)
            {
                var reachable = await SeededTargetIsReachableAsync(instance, relativePath, cancellationToken);
                if (!reachable.IsSuccess)
                    return Result.Failure<SettingEditSummary>(reachable.Error!);
            }

            var source = await ReadEditSourceAsync(instance, sourcePath, seeded, cancellationToken);
            if (!source.IsSuccess)
                return Result.Failure<SettingEditSummary>(source.Error!);

            var edit = SettingEdit.Apply(source.Value!, settingKey, settingValue);
            return edit.Outcome switch
            {
                SettingEditOutcome.Applied => Result.Success(
                    new SettingEditSummary(edit.Content!, edit.PreviousValue!, settingValue)),
                SettingEditOutcome.NoMatch when source.Value!.Trim().Length == 0 =>
                    Result.Failure<SettingEditSummary>(
                        $"'{sourcePath}' is empty, so it holds no settings to change. Pass the game's "
                        + "default/reference file as copy_from to fill it in."),
                // The key is the whole address here, so a miss is answerable: search_instance_files finds which
                // file actually carries it, which is a cheaper next step than guessing at spellings.
                SettingEditOutcome.NoMatch => Result.Failure<SettingEditSummary>(
                    $"'{settingKey}' is not a setting in '{sourcePath}'. Check the spelling against the "
                    + $"file, or find the file that does carry it with {Name(LlmTools.SearchFiles)}."),
                SettingEditOutcome.Ambiguous => Result.Failure<SettingEditSummary>(
                    $"'{settingKey}' is set in more than one place in '{sourcePath}', so which one was "
                    + $"meant is unknown. Name the exact text to replace with {Name(LlmTools.WriteFile)} instead."),
                SettingEditOutcome.NoChange => Result.Failure<SettingEditSummary>(
                    $"'{settingKey}' is already '{settingValue}' in '{sourcePath}', so this changes nothing."),
                _ => Result.Failure<SettingEditSummary>(
                    $"'{settingKey}' is not a setting name. Pass the key on its own, without an '=' or "
                    + "the rest of the line."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PrepareInstanceSettingEdit failed for {Instance} ({Path})", instance, relativePath);
            return Result.Failure<SettingEditSummary>(ex.Message);
        }
    }

    /// <summary>
    /// Refuses a seeded write whose target sits in a directory that does not exist.
    /// <para>
    /// A seeded write is the one path that reads a different file from the one it writes, so nothing
    /// else ever checks the target — and a caller guessing at a config's location lands here after the
    /// real path failed to read. Without this it stages happily and creates
    /// <c>Config/Linux_server/PalWorldSettings.ini</c> beside the real <c>Config/LinuxServer/</c>: a
    /// file the game will never read, proposed in a preview that looks entirely correct. A wrong path
    /// that fails loudly is recoverable; one that succeeds quietly is not.
    /// </para>
    /// </summary>
    private async Task<Result> SeededTargetIsReachableAsync(
        string instance, string relativePath, CancellationToken cancellationToken)
    {
        var separator = relativePath.LastIndexOfAny(['/', '\\']);
        if (separator <= 0)
            return Result.Success();

        var parent = relativePath[..separator];
        var list = await Task.Run(() => _files.List(instance, parent, MaxListEntries), cancellationToken);

        return list.Outcome switch
        {
            FileOpOutcome.Ok => Result.Success(),
            FileOpOutcome.NotFound or FileOpOutcome.NotAFile => Result.Failure(
                $"'{parent}' is not a directory in this instance, so '{relativePath}' is not where that "
                + $"file goes. Find the real path with {Name(LlmTools.FindFiles)} before writing to it."),
            FileOpOutcome.OutOfJail => Result.Failure(
                $"the path '{relativePath}' is outside the instance directory."),
            FileOpOutcome.InstanceUnavailable => Result.Failure($"'{instance}' is not a known instance."),
            _ => Result.Success(),
        };
    }

    /// <summary>
    /// The content an edit resolves against: the target file, or the reference file when the content is
    /// seeded from one. Every refusal comes back as a message naming what to do instead, because the
    /// caller is a model that will otherwise retry the same call.
    /// </summary>
    private async Task<Result<string>> ReadEditSourceAsync(
        string instance, string sourcePath, bool seeded, CancellationToken cancellationToken)
    {
        var read = await Task.Run(
            () => _files.Read(instance, sourcePath, MaxEditSourceBytes), cancellationToken);

        if (read.Outcome == FileOpOutcome.Ok)
            return Result.Success(read.Value!.Content);

        return Result.Failure<string>(read.Outcome switch
        {
            FileOpOutcome.NotFound when seeded =>
                $"'{sourcePath}' does not exist, so there is nothing to copy from. Find the real "
                + $"reference file with {Name(LlmTools.FindFiles)} or {Name(LlmTools.ListFiles)} first.",
            FileOpOutcome.NotFound =>
                $"'{sourcePath}' does not exist yet. To create it from the game's default/reference "
                + "file, pass that file's path as copy_from; its content is copied here and your "
                + "replacement applied to the copy.",
            FileOpOutcome.OutOfJail => $"the path '{sourcePath}' is outside the instance directory.",
            FileOpOutcome.NotAFile =>
                $"'{sourcePath}' isn't a regular file (it may be a directory, socket, pipe, or device).",
            FileOpOutcome.Binary => $"'{sourcePath}' isn't a text file, so it cannot be edited as text.",
            FileOpOutcome.TooLarge =>
                $"'{sourcePath}' is over the {MaxEditSourceBytes / 1024} KB edit limit.",
            FileOpOutcome.InstanceUnavailable => $"'{instance}' is not a known instance.",
            _ => read.Message ?? $"could not read '{sourcePath}'.",
        });
    }

    private async Task<Result> RunAsync(
        string op, string instance, Func<KgsmResult> action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(action, cancellationToken);
            if (result.IsSuccess)
                return Result.Success();

            _logger.LogWarning("{Op} failed for {Instance}: {Error}", op, instance, result.Stderr);
            return Result.Failure(result.Stderr ?? "unknown error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Op} threw for {Instance}", op, instance);
            return Result.Failure(ex.Message);
        }
    }
}
