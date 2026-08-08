using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IUpnpInfo"/> port by calling kgsm-lib's
/// <see cref="IWatchdogClient"/> — the single typed client for the kgsm-watchdog, the sole owner of
/// router / UPnP port forwarding. Maps the watchdog's UPnP result types onto the assistant's neutral
/// <see cref="UpnpReading"/> at this boundary, so the assistant domain never references kgsm-lib's
/// watchdog types.
/// <para>
/// Read-only, because the forwards are not this leaf's to drive: the watchdog opens them when an
/// instance starts and releases them when it stops. Per the port contract this NEVER throws, and the two
/// honest-unknown layers are preserved: the client returns <c>null</c> only when the daemon is
/// unreachable (→ <see cref="UpnpState.DaemonUnavailable"/>), and an in-body <c>state:"unavailable"</c>
/// means the daemon was reached but the router was not (→ <see cref="UpnpState.RouterUnavailable"/>) —
/// distinct from a real query that owns no forwards (→ <see cref="UpnpState.Queried"/> with an empty
/// list).
/// </para>
/// </summary>
internal sealed class KgsmUpnpInfo : IUpnpInfo
{
    private readonly IWatchdogClient _watchdog;
    private readonly ILogger<KgsmUpnpInfo> _logger;

    public KgsmUpnpInfo(IWatchdogClient watchdog, ILogger<KgsmUpnpInfo> logger)
    {
        _watchdog = watchdog;
        _logger = logger;
    }

    public async Task<UpnpReading> GetForwardsAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _watchdog.GetUpnpAsync(instance, cancellationToken).ConfigureAwait(false);

            // null ⇒ the daemon itself is unreachable (a graceful client null, not an exception).
            if (list is null)
                return new UpnpReading(UpnpState.DaemonUnavailable, Array.Empty<UpnpForward>());

            // Daemon reached, but the router couldn't be queried (no IGD / timeout) — honest, never "none".
            if (!string.Equals(list.State, "queried", StringComparison.Ordinal))
                return new UpnpReading(UpnpState.RouterUnavailable, Array.Empty<UpnpForward>());

            var forwards = list.Mappings
                .Select(m => new UpnpForward(m.ExternalPort, m.Protocol, m.InternalPort, m.InternalClient))
                .ToArray();
            return new UpnpReading(UpnpState.Queried, forwards);
        }
        catch (Exception ex)
        {
            // Any unexpected failure is failure-as-data — the watchdog is treated as unreachable.
            _logger.LogDebug(ex, "watchdog UPnP read failed for instance '{Instance}'", instance);
            return new UpnpReading(UpnpState.DaemonUnavailable, Array.Empty<UpnpForward>());
        }
    }
}
