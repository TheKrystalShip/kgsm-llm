using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies <see cref="IHostFacts"/> from KGSM.Lib's <see cref="ISystemService"/> (the host's
/// vitals, read in one <c>GetSystemInfo</c> call) and <see cref="INetworkService"/> (bound ports and
/// port conflicts). Both shell out through the process runner and are synchronous, so calls are
/// offloaded with <see cref="Task.Run(Action)"/>.
/// <para>
/// The host's own lifecycle verbs on <see cref="ISystemService"/> — <c>Shutdown</c>, <c>Restart</c>,
/// <c>CancelScheduled</c> — are deliberately not reached from here. Nothing in a chat turn may
/// propose rebooting the machine, so the capability stops at this seam rather than being gated
/// further up where a later change could quietly ungate it.
/// </para>
/// <para>
/// Per the port contract these never throw: a failure maps to
/// <see cref="FactsState.Unavailable"/>, which a surface reports as unknown rather than as an
/// idle host.
/// </para>
/// </summary>
internal sealed class KgsmHostFacts : IHostFacts
{
    private readonly ISystemService _system;
    private readonly INetworkService _network;
    private readonly IInstanceService _instances;
    private readonly ILogger<KgsmHostFacts> _logger;

    public KgsmHostFacts(
        ISystemService system,
        INetworkService network,
        IInstanceService instances,
        ILogger<KgsmHostFacts> logger)
    {
        _system = system;
        _network = network;
        _instances = instances;
        _logger = logger;
    }

    public async Task<HostFacts> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await Task.Run(() => _system.GetSystemInfo(), cancellationToken);
            if (info is null)
            {
                _logger.LogWarning("Host system info was unavailable.");
                return new HostFacts(FactsState.Unavailable, null, null, null, null, null, null);
            }

            return new HostFacts(
                FactsState.Available,
                HostDiskMapping.NullIfEmpty(info.Uptime),
                new HostLoad(info.Load.OneMin, info.Load.FiveMin, info.Load.FifteenMin),
                new HostMemory(info.Memory.Total, info.Memory.Used, info.Memory.Free, info.Memory.Available),
                HostDiskMapping.From(info.Disk),
                HostDiskMapping.NullIfEmpty(info.Network.ExternalIp),
                info.RebootRequired);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Host facts read failed.");
            return new HostFacts(FactsState.Unavailable, null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// Reads the host's listening ports and the engine's conflict findings, each as typed entries.
    /// <para>
    /// The two scans carry their own state: null from either means it could not be made, and an
    /// empty list means it was made and found nothing. Collapsing those together is how a conflict
    /// scan nobody managed to run comes back as "all clear".
    /// </para>
    /// <para>
    /// A listening port is joined to the instance configured for it, using the engine's own
    /// instance list. The process name is a binary's name (<c>java</c>, <c>PalServer-Linux</c>) and
    /// several instances can share one, so it is never what the join is made on.
    /// </para>
    /// </summary>
    public async Task<HostPortUsage> GetPortUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var usedTask = Task.Run(() => _network.ListUsedPortsDetailed(), cancellationToken);
            var conflictsTask = Task.Run(() => _network.FindConflictsDetailed(), cancellationToken);
            var owners = await ReadPortOwnersAsync(cancellationToken);

            var used = await usedTask;
            var conflicts = await conflictsTask;

            if (used is null)
                _logger.LogWarning("Host port listing could not be read.");
            if (conflicts is null)
                _logger.LogWarning("Host port conflict scan could not be read.");

            var ports = used is null
                ? []
                : used.Select(p => new HostPortEntry(
                        p.Port,
                        p.Protocol,
                        HostDiskMapping.NullIfEmpty(p.Process),
                        owners.GetValueOrDefault((p.Port, p.Protocol))))
                    .ToArray();

            var findings = conflicts is null
                ? []
                : conflicts.Select(c => new PortConflictEntry(
                        c.Port, c.Protocol, c.Instance, c.Other,
                        OtherIsInstance: string.Equals(c.Kind, "instance", StringComparison.Ordinal)))
                    .ToArray();

            return new HostPortUsage(
                used is null ? FactsState.Unavailable : FactsState.Available,
                ports,
                conflicts is null ? FactsState.Unavailable : FactsState.Available,
                findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Host port usage read failed.");
            return new HostPortUsage(FactsState.Unavailable, [], FactsState.Unavailable, []);
        }
    }

    /// <summary>
    /// Which instance is configured for each (port, protocol), from the engine's instance list.
    /// <para>
    /// A range is unrolled and a proto-less entry covers both protocols, so the map is keyed the
    /// same way a listening socket is observed. A read that fails yields an empty map: the ports
    /// are still reported, just without an owner beside them — the join is an enrichment, and
    /// losing it must not lose the measurement.
    /// </para>
    /// </summary>
    private async Task<Dictionary<(int Port, string Protocol), string>> ReadPortOwnersAsync(
        CancellationToken cancellationToken)
    {
        var owners = new Dictionary<(int, string), string>();

        try
        {
            var instances = await Task.Run(() => _instances.GetAllOrNull(), cancellationToken);
            if (instances is null)
                return owners;

            foreach (var entry in instances)
            {
                var mappings = entry.Value?.Ports;
                if (mappings is null)
                    continue;

                foreach (var (port, protocol) in mappings.Expand())
                    owners.TryAdd((port, protocol), entry.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read which instances own which ports.");
        }

        return owners;
    }
}
