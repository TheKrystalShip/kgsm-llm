using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Network;

/// <summary>
/// The outcome of a <c>get_network</c> read — whether the host-firewall authority could be reached at
/// all. Only <see cref="Available"/> carries a card (a real, measured firewall picture); a
/// <see cref="FirewallUnavailable"/> read is an honest "couldn't read", NOT a claim that no ports are
/// open, and stays summary-only.
/// </summary>
public enum NetworkState
{
    /// <summary>The firewall authority answered — the backend, enforcement, and rules are real reads.</summary>
    Available,

    /// <summary>The firewall authority could not be reached — an honest "couldn't read", never
    /// evidence that nothing is open. Everything else on the reading is absent.</summary>
    FirewallUnavailable,
}

/// <summary>
/// Whether the firewall backend could enumerate the rules it owns (the neutral mirror of kgsm-firewall's
/// list status). <see cref="Unknown"/> is the honest "the backend cannot tell" — it must NEVER be read
/// as "nothing is open" (a measured empty set vs. an unanswerable query).
/// </summary>
public enum PortListState
{
    /// <summary>The backend enumerated its owned rules (the set may legitimately be empty).</summary>
    Enumerated,

    /// <summary>The backend genuinely cannot enumerate rules — honest unknown, not an empty set.</summary>
    Unknown,

    /// <summary>The active backend does not support listing (e.g. no firewall backend present).</summary>
    Unsupported,
}

/// <summary>
/// The backend's runtime enforcement state, orthogonal to whether any rules exist. An installed backend
/// can be <see cref="Inactive"/> (e.g. ufw disabled), in which case it filters NOTHING — so every port
/// is reachable regardless of rules. NEVER read an inactive backend's empty rule set as "closed".
/// </summary>
public enum NetworkEnforcement
{
    /// <summary>The backend is active and filtering — owned rules determine open/closed.</summary>
    Enforcing,

    /// <summary>Installed but not enforcing (e.g. ufw inactive) — nothing filtered, all ports open.</summary>
    Inactive,

    /// <summary>Cannot determine (non-root / backend absent / unparsable) — honest unknown.</summary>
    Unknown,
}

/// <summary>One contiguous host-firewall port range for a single protocol, in the neutral card shape
/// (a single port has <see cref="Start"/> == <see cref="End"/>).</summary>
/// <param name="Start">First port of the inclusive range.</param>
/// <param name="End">Last port of the inclusive range.</param>
/// <param name="Protocol">Transport protocol — <c>"tcp"</c> or <c>"udp"</c>.</param>
public sealed record PortRule(int Start, int End, string Protocol)
{
    /// <summary>Renders one rule back to a compact spec — <c>start-end/proto</c> for a range,
    /// <c>port/proto</c> for a single port.</summary>
    public string ToDisplay() =>
        Start == End ? $"{Start}/{Protocol}" : $"{Start}-{End}/{Protocol}";
}

/// <summary>
/// The outcome of the router / UPnP half of a <c>get_network</c> read — whether the router's forwards
/// could be read, and via which layer they failed. Honest states, never collapsed: a router that
/// answered with no forwards (<see cref="Queried"/>, empty <c>Forwards</c>) is a real "nothing forwarded",
/// distinct from a router that couldn't be reached (<see cref="RouterUnavailable"/>) or a watchdog that
/// couldn't be reached at all (<see cref="DaemonUnavailable"/>) — neither of the latter is ever read as
/// "nothing is forwarded".
/// </summary>
public enum UpnpState
{
    /// <summary>The router (IGD) was queried — <c>Forwards</c> is what it owns for this instance (may be empty).</summary>
    Queried,

    /// <summary>The watchdog was reached but the router could not be queried (no IGD, or a timeout) —
    /// honest "couldn't ask the router", never "nothing forwarded".</summary>
    RouterUnavailable,

    /// <summary>The watchdog daemon itself could not be reached, so router forwarding is unknown.</summary>
    DaemonUnavailable,
}

/// <summary>One router / UPnP port-forward the IGD owns for an instance (measured from the router,
/// never fabricated): an external port mapped to an internal client + port.</summary>
/// <param name="ExternalPort">The WAN-side port the router forwards.</param>
/// <param name="Protocol">Transport protocol — <c>"tcp"</c> or <c>"udp"</c>.</param>
/// <param name="InternalPort">The LAN-side port the forward targets.</param>
/// <param name="InternalClient">The LAN-side client address the forward targets.</param>
public sealed record UpnpForward(int ExternalPort, string Protocol, int InternalPort, string InternalClient)
{
    /// <summary>Renders one forward compactly — <c>ext/proto→client:int</c> (or <c>ext/proto</c> when the
    /// external and internal ports match, the common case).</summary>
    public string ToDisplay() =>
        ExternalPort == InternalPort
            ? $"{ExternalPort}/{Protocol}"
            : $"{ExternalPort}/{Protocol}→{InternalClient}:{InternalPort}";
}

/// <summary>
/// The <c>get_network</c> tool's structured card payload (the surface half of its
/// <see cref="ToolResult{TData}"/>): one instance's network picture across TWO independent authorities —
/// the HOST FIREWALL (the ports KGSM has opened, the active backend, its enforcement state) and the
/// ROUTER / UPnP forwards the IGD owns for it. The two axes fail independently and are never conflated: a
/// firewall-unavailable read (<see cref="NetworkState.FirewallUnavailable"/>) can still carry a router
/// answer and vice-versa, and each honest-unknown state is preserved (the ecosystem never-fabricate rule).
/// A card is rendered whenever either axis has real measured structure (firewall
/// <see cref="NetworkState.Available"/> or UPnP <see cref="UpnpState.Queried"/>).
/// </summary>
/// <param name="Instance">The resolved instance name the picture is for (also the card subject id).</param>
/// <param name="State">Whether the firewall authority could be read (only <see cref="NetworkState.Available"/> carries values).</param>
/// <param name="Backend">The detected firewall backend token (e.g. <c>"ufw"</c>, <c>"none"</c>); empty when unavailable.</param>
/// <param name="ListState">Whether the owned rules could be enumerated (<see cref="PortListState.Unknown"/> ≠ empty).</param>
/// <param name="Enforcement">The backend's runtime enforcement state (load-bearing: an inactive backend filters nothing).</param>
/// <param name="Ports">The host-firewall ports KGSM owns for this instance; authoritative only when <see cref="ListState"/> is <see cref="PortListState.Enumerated"/>.</param>
/// <param name="UpnpState">Whether the router's forwards could be read (a separate authority — the watchdog — from the host firewall).</param>
/// <param name="Forwards">The router / UPnP forwards the IGD owns for this instance; authoritative only when <see cref="UpnpState"/> is <see cref="UpnpState.Queried"/>.</param>
public sealed record NetworkData(
    string Instance,
    NetworkState State,
    string Backend,
    PortListState ListState,
    NetworkEnforcement Enforcement,
    IReadOnlyList<PortRule> Ports,
    UpnpState UpnpState,
    IReadOnlyList<UpnpForward> Forwards);
