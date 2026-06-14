using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Kgsm;

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
    private readonly ILogger<KgsmServerOperations> _logger;

    public KgsmServerOperations(
        IInstanceService instances, ISystemService system, ILogger<KgsmServerOperations> logger)
    {
        _instances = instances;
        _system = system;
        _logger = logger;
    }

    public Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StartAsync), instance, () => _instances.Start(instance), cancellationToken);

    public Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StopAsync), instance, () => _instances.Stop(instance), cancellationToken);

    public Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(RestartAsync), instance, () => _instances.Restart(instance), cancellationToken);

    public Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(CreateBackupAsync), instance, () => _instances.CreateBackup(instance), cancellationToken);

    public Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(UpdateAsync), instance, () => _instances.Update(instance), cancellationToken);

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
            var found = await Task.Run(() => _instances.FindConfigPath(instance), cancellationToken);
            if (found.IsFailure)
                return Result.Failure<string>(
                    string.IsNullOrWhiteSpace(found.Stderr)
                        ? $"'{instance}' is not a known instance."
                        : found.Stderr.Trim());

            var configPath = found.Stdout?.Trim();
            // Guard empty (a CWD-relative read would otherwise escape into the
            // service's own working directory).
            if (string.IsNullOrWhiteSpace(configPath))
                return Result.Failure<string>($"'{instance}' has no resolvable config location.");

            var boundary = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (string.IsNullOrEmpty(boundary))
                return Result.Failure<string>($"'{instance}' has no resolvable config directory.");

            var dirInfo = new DirectoryInfo(boundary);
            if (!dirInfo.Exists)
                return Result.Failure<string>("The instance directory does not exist.");

            // Canonicalize the boundary (resolve a symlinked instance dir to its target).
            var realDir = dirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dirInfo.FullName;

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

    private static async Task<string> ReadCappedTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var toRead = (int)Math.Min(stream.Length, maxBytes);
        var buffer = new byte[toRead];
        var read = await stream.ReadAtLeastAsync(buffer, toRead, throwOnEndOfStream: false, cancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        return stream.Length > maxBytes ? text + "\n… (truncated)" : text;
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

            var snapshot = new InstanceHealthSnapshot(
                Running: status.Status,
                RecentLogLines: logLines,
                UpdatesAvailable: status.Version.UpdatesAvailable,
                CurrentVersion: NullIfEmpty(status.Version.Current),
                LatestVersion: status.Version.Latest,
                HostDisk: hostDisk,
                HostDiskUnavailableReason: diskReason);

            return Result.Success(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHealthSnapshot failed for {Instance}", instance);
            return Result.Failure<InstanceHealthSnapshot>(ex.Message);
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
            await Task.Run(() => _instances.Install(blueprint, null, null, instanceName), cancellationToken);
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
            await Task.Run(() => _instances.Uninstall(instance), cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uninstall failed for {Instance}", instance);
            return Result.Failure(ex.Message);
        }
    }

    public Task<Result> SetInstanceConfigValueAsync(
        string instance, string key, string value, CancellationToken cancellationToken = default) =>
        // RunAsync surfaces kgsm's stderr on a non-zero exit, so a denylisted/invalid key
        // (kgsm owns that policy) reaches the user as the failed Result's message.
        RunAsync(nameof(SetInstanceConfigValueAsync), instance,
            () => _instances.SetInstanceConfigValue(instance, key, value), cancellationToken);

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
