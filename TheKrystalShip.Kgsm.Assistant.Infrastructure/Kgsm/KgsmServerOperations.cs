using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IServerOperations"/> port by calling KGSM.Lib's
/// <see cref="IInstanceService"/> directly. We depend on the instance service (which shells
/// out to kgsm via a process runner) rather than the full <c>IKgsmClient</c> ON PURPOSE:
/// constructing <c>IKgsmClient</c> auto-starts KGSM.Lib's Unix-socket event listener, which
/// would contend with the Discord bot for the single kgsm event socket. This service ingests
/// events over the HTTP webhook instead, so it must never bind that socket.
/// <para>
/// The KGSM.Lib instance service is synchronous, so calls are offloaded with
/// <see cref="Task.Run(Action)"/>. Per the port contract these never throw — failures map
/// to a failed <see cref="Result"/>.
/// </para>
/// </summary>
internal sealed class KgsmServerOperations : IServerOperations
{
    private readonly IInstanceService _instances;
    private readonly ISystemService _system;
    private readonly IWatcherService _watcher;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<KgsmServerOperations> _logger;

    // kgsm's `watcher ports test` exit code when the instance has no ports configured — treated as
    // "not applicable" (the health ports check skips) rather than "not reachable".
    private const int NoPortsConfiguredExit = 44; // EC_WATCHER_PORT_NOT_ACTIVE

    public KgsmServerOperations(
        IInstanceService instances, ISystemService system, IWatcherService watcher,
        IInvocationContext invocation, ILogger<KgsmServerOperations> logger)
    {
        _instances = instances;
        _system = system;
        _watcher = watcher;
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

    /// <summary>Cap on bytes read from a config file — bounds what flows into the model's context.</summary>
    private const int MaxFileBytes = 64 * 1024;

    public async Task<Result<string>> ReadInstanceFileAsync(
        string instance, string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve the config file's location THROUGH kgsm itself (`instances find`,
            // i.e. __find_instance_config) — the very same resolution config-get/config-set
            // use. Reconstructing the path C#-side from a JSON dir field is what the old
            // bug did: it bound to `install_dir`, which is the GAME install dir
            // (…/<inst>/install) — not where <name>.config.ini lives — so every real read
            // 404'd. (Empirically: install_dir=…/factorio-test/install, but the config is
            // at ~/.local/share/kgsm/instances/factorio/factorio-test/… via a symlink to
            // the working dir.) Deferring to the engine keeps this drift-proof as kgsm
            // reshapes instance layout. The resolved file's directory is the read boundary.
            var (realDir, resolveError) = await ResolveInstanceBoundaryAsync(instance, cancellationToken);
            if (resolveError is not null || realDir is null)
                return Result.Failure<string>(resolveError ?? "could not resolve the instance directory.");

            // Combine + normalize (defeats ".."), then confine to the boundary.
            var candidate = Path.GetFullPath(Path.Combine(realDir, relativePath));
            if (!IsWithin(realDir, candidate))
                return Result.Failure<string>("Refused: the requested file is outside the instance directory.");

            var fileInfo = new FileInfo(candidate);
            if (!fileInfo.Exists)
                return Result.Failure<string>("The requested file was not found.");

            // Re-check after resolving a final-component symlink (an in-dir link out).
            var realFile = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fileInfo.FullName;
            if (!IsWithin(realDir, realFile))
                return Result.Failure<string>("Refused: the requested file resolves outside the instance directory.");

            // Refuse non-regular files: opening a FIFO/socket blocks indefinitely, and a
            // device/special file isn't something the model should read. (System.IO can't see
            // the st_mode type bits, so this is a small stat() interop — see IsNonRegularFile.)
            if (IsNonRegularFile(realFile))
                return Result.Failure<string>(
                    "Refused: that path isn't a regular file (it may be a directory, socket, pipe, or device).");

            var text = await ReadCappedTextAsync(realFile, MaxFileBytes, cancellationToken);
            return Result.Success(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadInstanceFile failed for {Instance} ({Path})", instance, relativePath);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is <paramref name="dir"/> itself or a path
    /// beneath it. The trailing-separator check is what keeps a sibling whose name
    /// merely starts with the dir (e.g. <c>/opt/x/inst</c> vs <c>/opt/x/inst-evil</c>)
    /// from being admitted.
    /// </summary>
    private static bool IsWithin(string dir, string candidate)
    {
        var normalizedDir = dir.TrimEnd(Path.DirectorySeparatorChar);
        return candidate == normalizedDir ||
               candidate.StartsWith(normalizedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Max entries returned in one directory listing — bounds the model-facing text.</summary>
    private const int MaxListEntries = 200;

    private static async Task<string> ReadCappedTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite so a live log being written by a running server can still be read.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var total = stream.Length;
        var toRead = (int)Math.Min(total, maxBytes);
        var buffer = new byte[toRead];
        var read = await stream.ReadAtLeastAsync(buffer, toRead, throwOnEndOfStream: false, cancellationToken);

        // Binary guard: a NUL byte in the sampled bytes means this isn't text — don't dump
        // raw bytes into the model's context; report the size honestly instead.
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            return $"[binary file, {total:N0} bytes — not shown]";

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        return total > maxBytes
            ? text + $"\n… (truncated — showing the first {maxBytes / 1024} KB of {total:N0} bytes)"
            : text;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InstanceDirEntry>>> ListInstanceDirectoryAsync(
        string instance, string? relativeSubdir = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var (realDir, resolveError) = await ResolveInstanceBoundaryAsync(instance, cancellationToken);
            if (resolveError is not null || realDir is null)
                return Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    resolveError ?? "could not resolve the instance directory.");

            // Combine + normalize (defeats ".."), then confine to the boundary — same jail as the read path.
            var target = string.IsNullOrWhiteSpace(relativeSubdir)
                ? realDir
                : Path.GetFullPath(Path.Combine(realDir, relativeSubdir));
            if (!IsWithin(realDir, target))
                return Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    "Refused: that path is outside the instance directory.");

            var dirInfo = new DirectoryInfo(target);
            if (!dirInfo.Exists)
                return Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    "That isn't a directory in the server's folder (omit the subdirectory to list the top level).");

            // Re-check after resolving a final-component symlink (an in-dir link out).
            var realTarget = dirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dirInfo.FullName;
            if (!IsWithin(realDir, realTarget))
                return Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                    "Refused: that path resolves outside the instance directory.");

            var entries = await Task.Run(() =>
                new DirectoryInfo(realTarget)
                    .EnumerateFileSystemInfos()
                    .Select(e =>
                    {
                        var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                        long size = 0;
                        if (!isDir && e is FileInfo fi)
                        {
                            try { size = fi.Length; } catch { /* unreadable size → 0 */ }
                        }
                        return new InstanceDirEntry(e.Name, isDir, size);
                    })
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxListEntries)
                    .ToList(),
                cancellationToken);

            return Result.Success<IReadOnlyList<InstanceDirEntry>>(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListInstanceDirectory failed for {Instance} ({Subdir})", instance, relativeSubdir);
            return Result.Failure<IReadOnlyList<InstanceDirEntry>>(ex.Message);
        }
    }

    /// <summary>
    /// Resolves an instance's read/list boundary THROUGH kgsm (<c>instances find</c> →
    /// __find_instance_config) — the same resolution config-get/config-set use, kept
    /// drift-proof as kgsm reshapes instance layout. The resolved config file's DIRECTORY
    /// (the instance root, which holds install/, logs/, saves/ …) is the boundary, with a
    /// symlinked instance dir canonicalized to its target. Returns the real boundary path,
    /// or a human-readable error (never both).
    /// </summary>
    private async Task<(string? Dir, string? Error)> ResolveInstanceBoundaryAsync(
        string instance, CancellationToken cancellationToken)
    {
        var found = await Task.Run(() => _instances.FindConfigPath(instance), cancellationToken);
        if (found.IsFailure)
            return (null, string.IsNullOrWhiteSpace(found.Stderr)
                ? $"'{instance}' is not a known instance."
                : found.Stderr.Trim());

        var configPath = found.Stdout?.Trim();
        // Guard empty (a CWD-relative read would otherwise escape into the
        // service's own working directory).
        if (string.IsNullOrWhiteSpace(configPath))
            return (null, $"'{instance}' has no resolvable config location.");

        var boundary = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (string.IsNullOrEmpty(boundary))
            return (null, $"'{instance}' has no resolvable config directory.");

        var dirInfo = new DirectoryInfo(boundary);
        if (!dirInfo.Exists)
            return (null, "The instance directory does not exist.");

        var realDir = dirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dirInfo.FullName;
        return (realDir, null);
    }

    // --- non-regular-file detection (Linux) ---
    // System.IO exposes only permission bits (File.GetUnixFileMode), never the st_mode TYPE
    // bits, so a small stat() interop is the clean way to refuse a FIFO/socket/device before
    // we open it (opening a FIFO with no writer blocks forever). The whole stack is Linux.

    private const uint S_IFMT = 0xF000;   // file-type mask in st_mode
    private const uint S_IFREG = 0x8000;  // regular file
    // st_mode is a 32-bit mode_t at offset 24 in glibc's LP64 struct stat
    // (st_dev 8 + st_ino 8 + st_nlink 8). struct stat is 144 bytes there; 256 is ample.
    private const int StModeOffset = 24;

    // DllImport (not LibraryImport): the classic marshaller handles byte[]/string without
    // /unsafe, and this assistant Infrastructure runs JIT (no Native-AOT trim concerns).
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int NativeStat([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, byte[] statbuf);

    /// <summary>
    /// True if <paramref name="path"/> exists but is NOT a regular file (FIFO/socket/device/…).
    /// On any interop failure returns false (don't block a legitimate read) — the read is still
    /// size-capped, so the worst case degrades to the prior behavior, never a crash.
    /// </summary>
    private static bool IsNonRegularFile(string path)
    {
        try
        {
            var buf = new byte[256];
            if (NativeStat(path, buf) != 0)
                return false; // couldn't stat → let the normal open/read path report any error
            var mode = BitConverter.ToUInt32(buf, StModeOffset);
            return (mode & S_IFMT) != S_IFREG;
        }
        catch
        {
            return false; // interop unavailable → don't refuse legitimate reads
        }
    }

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

    public async Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            // Long-running; completion is also broadcast via events. Mirror the bot: run it
            // and report queued-successfully unless it throws synchronously.
            var (actor, origin) = Provenance();
            await Task.Run(() => _instances.Install(blueprint, null, null, instanceName, actor, origin), cancellationToken);
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

    /// <summary>Generous size cap for a whole-file overwrite — headroom for future non-config
    /// uses, not a target (see plan §"Resolved decisions" 2).</summary>
    private const int MaxWriteBytes = 10 * 1024 * 1024;

    /// <summary>Sibling suffix for the last-good backup written before an overwrite.</summary>
    private const string BackupSuffix = ".kgsmbak";

    public async Task<Result> WriteInstanceFileAsync(
        string instance, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);
            if (byteCount > MaxWriteBytes)
                return Result.Failure(
                    $"Refused: the content is {byteCount:N0} bytes, over the {MaxWriteBytes / (1024 * 1024)} MB limit.");

            // Same jail as the read path — resolved THROUGH kgsm (`instances find`), drift-proof.
            var (realDir, resolveError) = await ResolveInstanceBoundaryAsync(instance, cancellationToken);
            if (resolveError is not null || realDir is null)
                return Result.Failure(resolveError ?? "could not resolve the instance directory.");

            var candidate = Path.GetFullPath(Path.Combine(realDir, relativePath));
            if (!IsWithin(realDir, candidate))
                return Result.Failure("Refused: the target file is outside the instance directory.");

            var fileInfo = new FileInfo(candidate);
            if (fileInfo.Exists)
            {
                // Refuse a non-regular target (FIFO/socket/device) before ever touching it.
                if (IsNonRegularFile(candidate))
                    return Result.Failure(
                        "Refused: that path isn't a regular file (it may be a directory, socket, pipe, or device).");

                // Re-check after resolving a final-component symlink (an in-dir link out).
                var realFile = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fileInfo.FullName;
                if (!IsWithin(realDir, realFile))
                    return Result.Failure("Refused: the target file resolves outside the instance directory.");
                candidate = realFile;
            }
            else
            {
                // New file: only inside an EXISTING in-jail directory — never creates deep trees.
                var parentDir = Path.GetDirectoryName(candidate);
                if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                    return Result.Failure(
                        "Refused: the containing directory doesn't exist — only a file inside an existing " +
                        "server directory can be created.");
                if (!IsWithin(realDir, parentDir))
                    return Result.Failure("Refused: the target directory is outside the instance directory.");

                var dirInfo = new DirectoryInfo(parentDir);
                var realParent = dirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dirInfo.FullName;
                if (!IsWithin(realDir, realParent))
                    return Result.Failure("Refused: the target directory resolves outside the instance directory.");
            }

            // Backup-before-overwrite: a non-empty existing target is copied ONCE to a sibling
            // ".kgsmbak" (overwritten each time — last-good, not a history) before it's replaced.
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
            {
                var backupPath = candidate + BackupSuffix;
                await Task.Run(() => File.Copy(candidate, backupPath, overwrite: true), cancellationToken);
            }

            // Atomic write: a temp file in the SAME directory, then an atomic rename — a
            // live-reading server never sees a torn file. UTF-8, no BOM.
            var directory = Path.GetDirectoryName(candidate)!;
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(candidate)}.kgsmtmp-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(tempPath, content, new System.Text.UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, candidate, overwrite: true);

            return Result.Success();
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
