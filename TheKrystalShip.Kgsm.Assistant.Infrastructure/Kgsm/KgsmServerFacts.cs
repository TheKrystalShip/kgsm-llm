using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies <see cref="IServerFacts"/> from KGSM.Lib's <see cref="IInstanceService"/> (backups and
/// versions, shelled out through the process runner) and <see cref="IWatchdogClient"/> (player
/// presence, the boot-autostart set, and the console ring — all HTTP-over-socket against the
/// supervisor, which starts no listener).
/// <para>
/// Per the port contract these never throw: an unreachable authority maps to
/// <see cref="FactsState.Unavailable"/>, which is reported as unknown and never as an empty world.
/// The watchdog legs additionally fail closed to <c>Unavailable</c> so the assistant still answers
/// on a host where the supervisor is down.
/// </para>
/// </summary>
internal sealed class KgsmServerFacts : IServerFacts
{
    private readonly IInstanceService _instances;
    private readonly IWatchdogClient _watchdog;
    private readonly ILogger<KgsmServerFacts> _logger;

    public KgsmServerFacts(
        IInstanceService instances, IWatchdogClient watchdog, ILogger<KgsmServerFacts> logger)
    {
        _instances = instances;
        _watchdog = watchdog;
        _logger = logger;
    }

    /// <summary>
    /// Lists an instance's backups from the manifest-backed JSON read.
    /// <para>
    /// That read collapses "no backups" and "the read failed" into the same empty list, so an empty
    /// result is re-asked through the plain command purely to learn which one it was. Reporting a
    /// failed read as "no backups" would state something the host does not know — the one thing this
    /// costs an extra call to avoid, and only on the empty path.
    /// </para>
    /// </summary>
    public async Task<BackupListing> GetBackupsAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var detailed = await Task.Run(() => _instances.GetBackupsDetailed(instance), cancellationToken);
            if (detailed.Count > 0)
            {
                var rows = detailed
                    .OrderByDescending(b => b.CreatedAt ?? DateTimeOffset.MinValue)
                    .Select(b => new BackupEntry(
                        b.Id,
                        HostDiskMapping.NullIfEmpty(b.Version),
                        b.CreatedAt,
                        b.SizeBytes,
                        HostDiskMapping.NullIfEmpty(b.Consistency)))
                    .ToArray();
                return new BackupListing(FactsState.Available, rows);
            }

            var probe = await Task.Run(() => _instances.GetBackups(instance), cancellationToken);
            if (!probe.IsSuccess)
            {
                _logger.LogWarning("Backup listing failed for {Instance}: {Error}", instance, probe.Stderr);
                return new BackupListing(FactsState.Unavailable, []);
            }

            return new BackupListing(FactsState.Available, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup listing failed for {Instance}", instance);
            return new BackupListing(FactsState.Unavailable, []);
        }
    }

    /// <summary>
    /// Reads the instance's version triple from a non-fast status read, which is what makes the engine
    /// actually consult upstream — so <see cref="VersionFacts.UpdateAvailable"/> is a real tri-state
    /// rather than the cached answer a fast read returns. For a Steam-backed game that upstream check
    /// is the slow part of the call.
    /// </summary>
    public async Task<VersionFacts> GetVersionAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await Task.Run(() => _instances.GetInstanceStatus(instance), cancellationToken);
            if (status is null)
            {
                _logger.LogWarning("Version read returned no status for {Instance}", instance);
                return new VersionFacts(FactsState.Unavailable, null, null, null);
            }

            var version = status.Version;
            return new VersionFacts(
                FactsState.Available,
                HostDiskMapping.NullIfEmpty(version.Current),
                HostDiskMapping.NullIfEmpty(version.Latest),
                // Null when the engine did not check — never coerced to "up to date".
                version.Checked ? version.UpdatesAvailable : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Version read failed for {Instance}", instance);
            return new VersionFacts(FactsState.Unavailable, null, null, null);
        }
    }

    public async Task<PresenceReading> GetPresenceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var map = await _watchdog.GetPlayerPresenceAsync(cancellationToken);

            // Null is the supervisor being unreachable, which is not an empty host.
            if (map is null)
                return new PresenceReading(FactsState.Unavailable, []);

            var rows = map
                .Select(kv => new InstancePresence(
                    kv.Key,
                    ParseDetection(kv.Value.Detection),
                    kv.Value.Players
                        .Select(p => new PlayerEntry(
                            HostDiskMapping.NullIfEmpty(p.Id),
                            HostDiskMapping.NullIfEmpty(p.Name)))
                        .ToArray()))
                .OrderBy(p => p.Instance, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new PresenceReading(FactsState.Available, rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Player presence read failed.");
            return new PresenceReading(FactsState.Unavailable, []);
        }
    }

    public async Task<AutostartReading> GetAutostartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var names = await _watchdog.GetEnabledNamesAsync(cancellationToken);
            return new AutostartReading(
                FactsState.Available,
                names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autostart set read failed.");
            return new AutostartReading(FactsState.Unavailable, []);
        }
    }

    public async Task<ConsoleTail> GetConsoleTailAsync(
        string instance, int lines, CancellationToken cancellationToken = default)
    {
        try
        {
            var tail = await _watchdog.GetConsoleTailAsync(instance, lines, cancellationToken);
            return new ConsoleTail(FactsState.Available, tail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console tail read failed for {Instance}", instance);
            return new ConsoleTail(FactsState.Unavailable, []);
        }
    }

    public async Task<ConsoleRuns> GetConsoleRunsAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var runs = await _watchdog.GetConsoleRunsAsync(instance, cancellationToken);
            return new ConsoleRuns(FactsState.Available, runs
                .Select(r => new ConsoleRunInfo(
                    r.Index,
                    r.Current,
                    // The supervisor reports UTC; carrying the offset explicitly keeps the
                    // comparison against an event's timestamp from depending on this host's zone.
                    r.EndedAt is null ? null : new DateTimeOffset(r.EndedAt.Value, TimeSpan.Zero)))
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console run list read failed for {Instance}", instance);
            return new ConsoleRuns(FactsState.Unavailable, []);
        }
    }

    public async Task<ConsoleTail> GetConsoleRunTailAsync(
        string instance, int lines, int run, CancellationToken cancellationToken = default)
    {
        try
        {
            var tail = await _watchdog.GetConsoleRunTailAsync(instance, lines, run, cancellationToken);
            return new ConsoleTail(FactsState.Available, tail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console run {Run} read failed for {Instance}", run, instance);
            return new ConsoleTail(FactsState.Unavailable, []);
        }
    }

    /// <summary>
    /// Maps the supervisor's detection token. An unrecognised token is
    /// <see cref="PresenceDetection.Unknown"/> rather than a guess — a token this build does not know
    /// is exactly a capability it cannot establish.
    /// </summary>
    private static PresenceDetection ParseDetection(string? detection) =>
        detection?.Trim().ToLowerInvariant() switch
        {
            "log" => PresenceDetection.Log,
            "rcon" => PresenceDetection.Rcon,
            "none" => PresenceDetection.None,
            _ => PresenceDetection.Unknown,
        };
}
