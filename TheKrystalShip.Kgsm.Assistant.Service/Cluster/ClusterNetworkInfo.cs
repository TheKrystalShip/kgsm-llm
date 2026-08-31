using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// What a node's firewall holds open for one of its servers.
/// </summary>
/// <remarks>
/// <para>
/// A node reports the backend it uses and, per port the server declares, whether that port is open —
/// which is the part a person asks about. It does not relay its firewall's own enumeration of every
/// rule it owns, so the reading here is narrower than the one taken beside the engine, and the
/// difference is stated rather than filled in.
/// </para>
/// <para>
/// <b>Enforcement is unknown, and that is a measurement.</b> Whether a node's firewall is enforcing or
/// merely installed is a fact about that machine's own daemon and does not cross the wire, so it is
/// reported as unknown rather than assumed from the fact that ports came back.
/// </para>
/// </remarks>
internal sealed class ClusterNetworkInfo(ClusterServers servers, NodeApiClient api) : INetworkInfo
{
    public async Task<NetworkReading> GetPortsAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? _) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Unreadable;

        NodeResult<Server> read = await api.GetAsync(
            node, $"/api/v1/servers/{Uri.EscapeDataString(instance)}", ApiContractsJson.Default.Server,
            cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body?.Network is not { } network)
            return Unreadable;

        // A port whose open state the node could not establish is left out rather than listed as a rule.
        // This list is what the firewall is holding open, and an unknown is not that.
        IReadOnlyList<PortRule> open =
        [
            .. network.Required
                .Where(p => p.Open == true)
                .Select(p => new PortRule(p.Port, p.Port, p.Proto)),
        ];

        return new NetworkReading(
            NetworkState.Available,
            network.Firewall,
            // Enumerated only when every declared port came back with an answer. One unknown among them
            // means the set below is short by an amount nobody can state, which is what Unknown says.
            network.Required.All(p => p.Open is not null) ? PortListState.Enumerated : PortListState.Unknown,
            NetworkEnforcement.Unknown,
            open);
    }

    /// <summary>The node could not be asked, so nothing is known about its firewall.</summary>
    private static NetworkReading Unreadable => new(
        NetworkState.FirewallUnavailable, string.Empty,
        PortListState.Unknown, NetworkEnforcement.Unknown, []);
}

/// <summary>
/// What a node's router forwards for one of its servers, which is nothing this member can see.
/// </summary>
/// <remarks>
/// A UPnP forward is read from the router by the supervisor on the machine that asked for it, and a
/// node does not relay that reading. Reported as the supervisor being unreachable — which from here it
/// is — rather than as an empty set of forwards, because an empty set is a measurement meaning the
/// router forwards nothing, and that is a different and much stronger claim.
/// </remarks>
internal sealed class ClusterUpnpInfo : IUpnpInfo
{
    public Task<UpnpReading> GetForwardsAsync(
        string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpnpReading(UpnpState.DaemonUnavailable, []));
}
