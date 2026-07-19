using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Network;

/// <summary>
/// The deterministic <c>get_network</c> composer (mirrors <see cref="Metrics.PerformanceReport"/>):
/// turns a neutral <see cref="NetworkReading"/> into a <see cref="NetworkData"/> card plus a
/// model-grounding <see cref="ToolResult{TData}.Summary"/>. Pure and I/O-free — the firewall read
/// happens in the port impl — so it is unit-testable without mocks and is the single home for how the
/// host-firewall picture is worded.
/// <para>
/// Honesty rules baked in: a <see cref="NetworkState.FirewallUnavailable"/> read is an honest "couldn't
/// read", explicitly NOT a claim that no ports are open; a <see cref="PortListState.Unknown"/> backend
/// is an honest unknown, never a fabricated empty "nothing open"; an <see cref="NetworkEnforcement.Inactive"/>
/// backend is called out (its rules aren't filtering, so ports are reachable regardless); and every
/// summary states that this covers the HOST FIREWALL only, never router/UPnP forwarding (which the host
/// cannot observe — the ecosystem never-fabricate rule).
/// </para>
/// </summary>
public static class NetworkReport
{
    // Every grounding sentence ends with this, so the model never implies the assistant can see or open
    // router/UPnP port forwarding — only the host firewall is in scope here.
    private const string HostFirewallCaveat =
        "This covers the host firewall only, not router/UPnP port forwarding (which isn't observable from the host).";

    /// <summary>
    /// Builds the result envelope for one instance's host-firewall read. Always returns a result: a
    /// firewall-unavailable reading degrades to an honest summary with a null-valued
    /// <see cref="NetworkData"/>, never an error.
    /// </summary>
    /// <param name="reading">The neutral, measured-or-absent reading from the network port.</param>
    /// <param name="instance">The resolved instance name (the result's subject).</param>
    public static ToolResult<NetworkData> Build(NetworkReading reading, string instance)
    {
        var data = new NetworkData(
            instance, reading.State, reading.Backend, reading.ListState, reading.Enforcement, reading.Ports);

        var (confidence, summary) = reading.State switch
        {
            NetworkState.Available => BuildAvailable(instance, reading),
            // "Couldn't read" — not measured, so only a possible conclusion, NOT a claim that nothing is open.
            _ => (Confidence.Possible,
                $"The firewall state for {instance} is unavailable right now — the host firewall service " +
                $"isn't reachable. That isn't a sign no ports are open; it just couldn't be read. {HostFirewallCaveat}"),
        };

        return new ToolResult<NetworkData>(
            Tool: LlmTools.GetNetwork,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Network, instance),
            Summary: summary,
            Data: data);
    }

    private static (Confidence, string) BuildAvailable(string instance, NetworkReading r)
    {
        // No backend that can enumerate rules — honest, and distinct from "nothing open".
        if (r.ListState == PortListState.Unsupported)
            return (Confidence.Confirmed,
                $"No host firewall backend is active on this host, so KGSM isn't managing any host-firewall " +
                $"port rules for {instance}. {HostFirewallCaveat}");

        if (r.ListState == PortListState.Unknown)
            return (Confidence.Possible,
                $"The host firewall backend ({BackendLabel(r.Backend)}) couldn't list its rules, so the open " +
                $"ports for {instance} are unknown right now — not a claim that none are open. {HostFirewallCaveat}");

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

        parts.Add(HostFirewallCaveat);
        return (Confidence.Confirmed, string.Join(" ", parts));
    }

    private static string BackendLabel(string backend) =>
        string.IsNullOrWhiteSpace(backend) ? "unknown" : backend;
}
