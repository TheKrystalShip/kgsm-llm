using Microsoft.Extensions.Logging;

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
    private readonly ILogger<KgsmServerOperations> _logger;

    // kgsm's `watcher ports test` exit code when the instance has no ports configured — treated as
    // "not applicable" (the health ports check skips) rather than "not reachable".
    private const int NoPortsConfiguredExit = 44; // EC_WATCHER_PORT_NOT_ACTIVE

    public KgsmServerOperations(
        IInstanceService instances, IInstanceFiles files, ISystemService system, IWatcherService watcher,
        IWatchdogClient watchdog, IInvocationContext invocation, ILogger<KgsmServerOperations> logger)
    {
        _instances = instances;
        _files = files;
        _system = system;
        _watcher = watcher;
        _watchdog = watchdog;
        _invocation = invocation;
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
        new(entry.Name, entry.Kind == FileKind.Dir, entry.SizeBytes ?? 0);

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
    /// supplies running-state, recent logs and the real update check; host disk comes
    /// from <c>system info</c>. A failed host read maps to a null disk + reason (the
    /// aggregator then skips the disk check) — never a fabricated <c>0%</c>.
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

            var logLines = SplitLogLines(status.RecentLogs);

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
                UpdatesAvailable: status.Version.UpdatesAvailable,
                CurrentVersion: NullIfEmpty(status.Version.Current),
                LatestVersion: status.Version.Latest,
                HostDisk: hostDisk,
                HostDiskUnavailableReason: diskReason,
                PortsReachable: portsReachable,
                PortsDetail: portsDetail);

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
        int? port = null)
    {
        try
        {
            // Long-running; completion is also broadcast via events. Mirror the bot: run it
            // and report queued-successfully unless it throws synchronously.
            var (actor, origin) = Provenance();
            await Task.Run(
                () => _instances.Install(
                    blueprint, installDir: null, version: version, name: instanceName,
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
