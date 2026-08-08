using TheKrystalShip.Kgsm.Assistant.Network;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One neutral reading of an instance's HOST-FIREWALL state, as the <see cref="INetworkInfo"/> port
/// returns it: the raw measured-or-absent inputs the <see cref="NetworkReport"/> aggregator turns into
/// a card. <see cref="State"/> carries the outcome — <see cref="NetworkState.Available"/> with real
/// backend/rules, or <see cref="NetworkState.FirewallUnavailable"/> with everything absent. Failure is
/// data, never an exception: an unreachable firewall authority maps to
/// <see cref="NetworkState.FirewallUnavailable"/>, so the port never throws. This describes the host
/// firewall ONLY — router / UPnP forwarding is not observable from the host and is never represented.
/// </summary>
public sealed record NetworkReading(
    NetworkState State,
    string Backend,
    PortListState ListState,
    NetworkEnforcement Enforcement,
    IReadOnlyList<PortRule> Ports);

/// <summary>
/// Host-firewall visibility for one instance — the neutral capability that backs the model-facing
/// <c>get_network</c> read. Read-only: an instance's ports are opened by the supervisor when it starts
/// and released when it stops, so there is no rule for this leaf to write. A leaf capability that
/// reaches the kgsm-firewall authority (the single owner of host-firewall rules); the host supplies the
/// adapter. Additive and fails closed: with no firewall authority reachable a read returns a
/// <see cref="NetworkState.FirewallUnavailable"/> result, so the assistant composes and boots
/// standalone. Implementations MUST NOT throw — every failure maps to the unavailable outcome. This
/// covers the host firewall ONLY; router/UPnP forwarding is a separate authority
/// (<see cref="IUpnpInfo"/>) and is never conflated with it.
/// </summary>
public interface INetworkInfo
{
    /// <summary>Read the host-firewall ports KGSM owns for one instance, plus the backend and its
    /// enforcement state.</summary>
    Task<NetworkReading> GetPortsAsync(string instance, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="INetworkInfo"/> for hosts that have not wired a firewall authority: every read
/// fails closed as <see cref="NetworkState.FirewallUnavailable"/>, so embedding the assistant library
/// never breaks DI just because kgsm-firewall isn't configured. <c>AddKgsmAssistant</c> registers this with
/// <c>TryAddSingleton</c>; a host that wires the firewall registers a concrete adapter afterward
/// (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableNetworkInfo : INetworkInfo
{
    public Task<NetworkReading> GetPortsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new NetworkReading(
            NetworkState.FirewallUnavailable, string.Empty,
            PortListState.Unknown, NetworkEnforcement.Unknown, Array.Empty<PortRule>()));
}
