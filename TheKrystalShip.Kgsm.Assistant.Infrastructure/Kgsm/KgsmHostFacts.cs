using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;

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
    private readonly ILogger<KgsmHostFacts> _logger;

    public KgsmHostFacts(ISystemService system, INetworkService network, ILogger<KgsmHostFacts> logger)
    {
        _system = system;
        _network = network;
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

    public async Task<HostPortUsage> GetPortUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var used = await Task.Run(() => _network.ListUsedPorts(), cancellationToken);
            var conflicts = await Task.Run(() => _network.FindConflicts(), cancellationToken);

            // Either leg failing makes the whole reading unavailable rather than half-reported: a
            // conflict list that silently came back empty because the command failed reads exactly
            // like "no conflicts", which is the one thing it must never be mistaken for.
            if (!used.IsSuccess || !conflicts.IsSuccess)
            {
                _logger.LogWarning(
                    "Host port usage read failed (used: {UsedOk}, conflicts: {ConflictsOk}).",
                    used.IsSuccess, conflicts.IsSuccess);
                return new HostPortUsage(FactsState.Unavailable, [], []);
            }

            return new HostPortUsage(FactsState.Available, Lines(used.Stdout), Lines(conflicts.Stdout));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Host port usage read failed.");
            return new HostPortUsage(FactsState.Unavailable, [], []);
        }
    }

    private static IReadOnlyList<string> Lines(string? stdout) =>
        string.IsNullOrWhiteSpace(stdout)
            ? []
            : stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
