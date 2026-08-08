using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="INetworkInfo"/> port by calling kgsm-lib's
/// <see cref="IFirewallService"/> — the single typed client for the kgsm-firewall authority (the owner
/// of host-firewall rules). Maps the authority's precise result types onto the assistant's neutral
/// <see cref="NetworkReading"/> at this boundary, so the assistant domain never references kgsm-lib's
/// firewall types.
/// <para>
/// Read-only, because the rules are not this leaf's to write: an instance's ports are opened by the
/// supervisor when it starts and released when it stops. Per the port contract this NEVER throws —
/// kgsm-lib throws <see cref="Exceptions.FirewallException"/> only when the authority is unreachable,
/// which maps to <see cref="NetworkState.FirewallUnavailable"/>: failure as data, never an exception.
/// Covers the HOST FIREWALL only; router/UPnP forwarding is the watchdog's, behind IUpnpInfo.
/// </para>
/// </summary>
internal sealed class KgsmNetworkInfo : INetworkInfo
{
    private readonly IFirewallService _firewall;
    private readonly ILogger<KgsmNetworkInfo> _logger;

    public KgsmNetworkInfo(IFirewallService firewall, ILogger<KgsmNetworkInfo> logger)
    {
        _firewall = firewall;
        _logger = logger;
    }

    public async Task<NetworkReading> GetPortsAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            // List is scoped to the instance; backend gives the detected backend token/name. Both hit the
            // same authority — if either fails the whole read is honestly "unavailable".
            var list = await _firewall.ListOwnedAsync(instance, cancellationToken).ConfigureAwait(false);
            var backend = await _firewall.BackendAsync(cancellationToken).ConfigureAwait(false);

            var ports = list.Rules
                .SelectMany(r => r.Ports)
                .Select(p => new PortRule(p.Start, p.End, p.Protocol))
                .ToArray();

            return new NetworkReading(
                NetworkState.Available,
                backend.Backend,
                MapListStatus(list.Status),
                MapEnforcement(list.Enforcement),
                ports);
        }
        catch (Exception ex)
        {
            // Unreachable authority (FirewallException) or any other failure is failure-as-data.
            _logger.LogDebug(ex, "firewall read failed for instance '{Instance}'", instance);
            return new NetworkReading(
                NetworkState.FirewallUnavailable, string.Empty,
                PortListState.Unknown, NetworkEnforcement.Unknown, Array.Empty<PortRule>());
        }
    }

    private static PortListState MapListStatus(FirewallListStatus status) => status switch
    {
        FirewallListStatus.Ok => PortListState.Enumerated,
        FirewallListStatus.Unsupported => PortListState.Unsupported,
        _ => PortListState.Unknown,
    };

    private static NetworkEnforcement MapEnforcement(FirewallEnforcement enforcement) => enforcement switch
    {
        FirewallEnforcement.Enforcing => NetworkEnforcement.Enforcing,
        FirewallEnforcement.Inactive => NetworkEnforcement.Inactive,
        _ => NetworkEnforcement.Unknown,
    };
}
