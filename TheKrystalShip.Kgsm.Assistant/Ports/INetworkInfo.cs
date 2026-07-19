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
/// The outcome of an <c>open_ports</c> execution against the host-firewall authority — the neutral
/// mirror of kgsm-firewall's precise apply result, preserved (never collapsed to a success bool) so the
/// confirm path can word it honestly.
/// </summary>
public enum NetworkActionState
{
    /// <summary>The rules were applied and the backend is enforcing (the ports are now open).</summary>
    Applied,

    /// <summary>The rules were written but the backend is NOT enforcing (e.g. ufw inactive): they
    /// persist and take effect on the next enable, and the ports are reachable meanwhile.</summary>
    AppliedInactive,

    /// <summary>Nothing to do — the desired state already held.</summary>
    NoOp,

    /// <summary>The active backend does not support opening ports (e.g. no firewall backend).</summary>
    Unsupported,

    /// <summary>The operation reached the backend but failed (e.g. the backend rejected the rule).</summary>
    Failed,

    /// <summary>The firewall authority could not be reached — nothing was changed.</summary>
    FirewallUnavailable,
}

/// <summary>The result of opening host-firewall ports for one instance.</summary>
/// <param name="State">The precise outcome.</param>
/// <param name="Backend">The active backend token (e.g. <c>"ufw"</c>); empty when unavailable.</param>
/// <param name="Detail">Human-readable detail from the authority, when present.</param>
public sealed record NetworkActionResult(NetworkActionState State, string Backend, string? Detail);

/// <summary>
/// Host-firewall visibility and control for one instance — the neutral capability that backs the
/// model-facing <c>get_network</c> read and the <c>open_ports</c> staged command. A leaf capability
/// that reaches the kgsm-firewall authority (the single owner of host-firewall rules); the host supplies
/// the adapter. Additive and fails closed: with no firewall authority reachable a read returns a
/// <see cref="NetworkState.FirewallUnavailable"/> result and an open returns
/// <see cref="NetworkActionState.FirewallUnavailable"/>, so the assistant composes and boots standalone.
/// Implementations MUST NOT throw — every failure maps to the unavailable outcome. This covers the host
/// firewall ONLY; it never opens or reports router/UPnP port forwarding (no such control surface exists
/// from the host).
/// </summary>
public interface INetworkInfo
{
    /// <summary>Read the host-firewall ports KGSM owns for one instance, plus the backend and its
    /// enforcement state.</summary>
    Task<NetworkReading> GetPortsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Open the given host-firewall <paramref name="ports"/> for one instance (the real
    /// mutation, run only from a confirmed <c>open_ports</c>). An empty port set is a no-op.</summary>
    Task<NetworkActionResult> OpenPortsAsync(
        string instance, IReadOnlyList<PortRule> ports, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="INetworkInfo"/> for hosts that have not wired a firewall authority: every read
/// fails closed as <see cref="NetworkState.FirewallUnavailable"/> and every open as
/// <see cref="NetworkActionState.FirewallUnavailable"/>, so embedding the assistant library never breaks
/// DI just because kgsm-firewall isn't configured. <c>AddKgsmAssistant</c> registers this with
/// <c>TryAddSingleton</c>; a host that wires the firewall registers a concrete adapter afterward
/// (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableNetworkInfo : INetworkInfo
{
    public Task<NetworkReading> GetPortsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new NetworkReading(
            NetworkState.FirewallUnavailable, string.Empty,
            PortListState.Unknown, NetworkEnforcement.Unknown, Array.Empty<PortRule>()));

    public Task<NetworkActionResult> OpenPortsAsync(
        string instance, IReadOnlyList<PortRule> ports, CancellationToken cancellationToken = default) =>
        Task.FromResult(new NetworkActionResult(NetworkActionState.FirewallUnavailable, string.Empty, null));
}
