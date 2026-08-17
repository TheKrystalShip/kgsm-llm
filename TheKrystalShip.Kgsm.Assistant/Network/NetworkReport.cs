using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Network;

/// <summary>
/// The deterministic <c>get_network</c> composer (mirrors <see cref="Metrics.PerformanceReport"/>):
/// turns a neutral host-firewall <see cref="NetworkReading"/> plus a router/UPnP <see cref="UpnpReading"/>
/// into a <see cref="NetworkData"/> card plus a model-grounding <see cref="ToolResult{TData}.Summary"/>.
/// Pure and I/O-free — both reads happen in the port impls — so it is unit-testable without mocks and is
/// the single home for how the network picture is worded.
/// <para>
/// Honesty rules baked in: a <see cref="NetworkState.FirewallUnavailable"/> read is an honest "couldn't
/// read", explicitly NOT a claim that no ports are open; a <see cref="PortListState.Unknown"/> backend is
/// an honest unknown, never a fabricated empty "nothing open"; an <see cref="NetworkEnforcement.Inactive"/>
/// backend is called out (its rules aren't filtering). The router/UPnP axis is independent and equally
/// honest: a <see cref="UpnpState.Queried"/> router that owns no forwards is a real "nothing forwarded",
/// distinct from <see cref="UpnpState.RouterUnavailable"/> (couldn't ask the router) and
/// <see cref="UpnpState.DaemonUnavailable"/> (router forwarding unknown) — neither is ever "nothing forwarded".
/// </para>
/// </summary>
public static class NetworkReport
{
    /// <summary>
    /// Builds the result envelope for one instance's network read across both authorities. Always returns
    /// a result: an unreachable authority on either axis degrades to an honest clause, never an error. The
    /// overall <see cref="Confidence"/> is the firewall axis's (the historically load-bearing one); the
    /// router axis adds a clause but does not lower confidence below what the firewall read supports.
    /// </summary>
    /// <param name="firewall">The neutral, measured-or-absent host-firewall reading.</param>
    /// <param name="upnp">The neutral, measured-or-absent router/UPnP reading.</param>
    /// <param name="instance">The resolved instance name (the result's subject).</param>
    public static ToolResult<NetworkData> Build(NetworkReading firewall, UpnpReading upnp, string instance)
    {
        var data = new NetworkData(
            instance, firewall.State, firewall.Backend, firewall.ListState, firewall.Enforcement,
            firewall.Ports, upnp.State, upnp.Forwards);

        var (confidence, firewallClause) = firewall.State switch
        {
            NetworkState.Available => BuildFirewallAvailable(instance, firewall),
            // "Couldn't read" — not measured, so only a possible conclusion, NOT a claim that nothing is open.
            _ => (Confidence.Possible,
                $"The host firewall state for {instance} is unavailable right now — the firewall service " +
                "isn't reachable. That isn't a sign no ports are open; it just couldn't be read."),
        };

        string routerClause = BuildRouterClause(instance, upnp);
        return new ToolResult<NetworkData>(
            Tool: ResultCardKinds.Network,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Network, instance),
            Summary: $"{firewallClause} {routerClause}",
            Data: data);
    }

    private static (Confidence, string) BuildFirewallAvailable(string instance, NetworkReading r)
    {
        // No backend that can enumerate rules — honest, and distinct from "nothing open".
        if (r.ListState == PortListState.Unsupported)
            return (Confidence.Confirmed,
                $"No host firewall backend is active on this host, so KGSM isn't managing any host-firewall " +
                $"port rules for {instance}.");

        if (r.ListState == PortListState.Unknown)
            return (Confidence.Possible,
                $"The host firewall backend ({BackendLabel(r.Backend)}) couldn't list its rules, so the open " +
                $"host-firewall ports for {instance} are unknown right now — not a claim that none are open.");

        // Enumerated — the rule set is authoritative (may be empty).
        var parts = new List<string>();
        if (r.Ports.Count == 0)
            parts.Add($"{instance} has no host-firewall ports opened by KGSM.");
        else
            parts.Add($"{instance} has these host-firewall ports open: {string.Join(", ", r.Ports.Select(p => p.ToDisplay()))}.");

        switch (r.Enforcement)
        {
            case NetworkEnforcement.Inactive:
                parts.Add($"The firewall backend ({BackendLabel(r.Backend)}) is installed but not enforcing, so " +
                          "these rules aren't filtering — every port is currently reachable regardless.");
                break;
            case NetworkEnforcement.Enforcing:
                parts.Add($"The firewall backend ({BackendLabel(r.Backend)}) is enforcing.");
                break;
            // Unknown enforcement adds no clause — never fabricate a state.
        }

        return (Confidence.Confirmed, string.Join(" ", parts));
    }

    // The router / UPnP axis — a separate authority (the watchdog) from the host firewall, so it is worded
    // as its own clause and each honest-unknown state is preserved (never read as "nothing forwarded").
    private static string BuildRouterClause(string instance, UpnpReading upnp) => upnp.State switch
    {
        UpnpState.Queried when upnp.Forwards.Count == 0 =>
            $"On the router (UPnP), {instance} has no port forwards.",
        UpnpState.Queried =>
            $"On the router (UPnP), {instance} is forwarding: {string.Join(", ", upnp.Forwards.Select(f => f.ToDisplay()))}.",
        UpnpState.RouterUnavailable =>
            "The router couldn't be queried for UPnP forwards right now (no reachable IGD) — not a sign nothing is forwarded.",
        // DaemonUnavailable
        _ => "Router/UPnP forwarding is unknown (the watchdog isn't reachable), so it couldn't be checked.",
    };

    private static string BackendLabel(string backend) =>
        string.IsNullOrWhiteSpace(backend) ? "unknown" : backend;
}
