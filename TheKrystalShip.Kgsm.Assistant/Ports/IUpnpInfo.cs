using TheKrystalShip.Kgsm.Assistant.Network;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One neutral reading of an instance's ROUTER / UPnP forwarding state, as the <see cref="IUpnpInfo"/>
/// port returns it: the measured-or-absent inputs the <see cref="NetworkReport"/> aggregator folds into
/// the network card alongside the host-firewall picture. <see cref="State"/> carries the outcome —
/// <see cref="UpnpState.Queried"/> with the router's forwards (possibly empty),
/// <see cref="UpnpState.RouterUnavailable"/> (watchdog reached, router not), or
/// <see cref="UpnpState.DaemonUnavailable"/> (watchdog itself unreachable). Failure is data, never an
/// exception — the port never throws.
/// </summary>
public sealed record UpnpReading(UpnpState State, IReadOnlyList<UpnpForward> Forwards);

/// <summary>
/// The outcome of an <c>open_ports</c> router leg against the watchdog's UPnP authority — the neutral
/// mirror of the watchdog's precise apply result, preserved (never collapsed to a success bool) so the
/// confirm path can word it honestly.
/// </summary>
public enum UpnpActionState
{
    /// <summary>The router (IGD) confirmed the forward — it is now open.</summary>
    Applied,

    /// <summary>Nothing was done — the instance has port-forwarding disabled (the config gate), or no
    /// ports to forward. NOT a failure, and never a fabricated open.</summary>
    Skipped,

    /// <summary>The watchdog was reached but could not deliver the forward (no IGD, or a router error).</summary>
    Failed,

    /// <summary>The watchdog daemon could not be reached — nothing was changed.</summary>
    Unavailable,
}

/// <summary>The result of opening router / UPnP forwards for one instance.</summary>
/// <param name="State">The precise outcome.</param>
/// <param name="Detail">Human-readable detail from the watchdog, when present.</param>
public sealed record UpnpActionResult(UpnpActionState State, string? Detail);

/// <summary>
/// Router / UPnP forwarding visibility and control for one instance — the neutral capability that lets
/// <c>get_network</c> observe an instance's router forwards and <c>open_ports</c> optionally drive them.
/// A leaf capability that reaches the kgsm-watchdog (the sole owner of UPnP forwarding, via its control
/// socket); the host supplies the adapter. Separate authority from the host firewall
/// (<see cref="INetworkInfo"/>) — never conflated. Additive and fails closed: with no watchdog reachable a
/// read returns a <see cref="UpnpState.DaemonUnavailable"/> result and an open returns
/// <see cref="UpnpActionState.Unavailable"/>, so the assistant composes and boots standalone.
/// Implementations MUST NOT throw — every failure maps to an unavailable outcome.
/// </summary>
public interface IUpnpInfo
{
    /// <summary>Read the router / UPnP forwards the IGD owns for one instance (tagged with its name).</summary>
    Task<UpnpReading> GetForwardsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Open router / UPnP forwards for the given <paramref name="ports"/> on one instance (the
    /// real mutation, run only from a confirmed <c>open_ports</c> with the router leg opted in). Honors the
    /// instance's <c>enable_port_forwarding</c> gate at the watchdog (a gated-off instance is
    /// <see cref="UpnpActionState.Skipped"/>); an empty port set forwards the instance's configured ports.</summary>
    Task<UpnpActionResult> OpenForwardsAsync(
        string instance, IReadOnlyList<PortRule> ports, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IUpnpInfo"/> for hosts that have not wired a watchdog client: every read fails
/// closed as <see cref="UpnpState.DaemonUnavailable"/> and every open as
/// <see cref="UpnpActionState.Unavailable"/>, so embedding the assistant library never breaks DI just
/// because the watchdog isn't configured. <c>AddKgsmAssistant</c> registers this with
/// <c>TryAddSingleton</c>; a host that wires the watchdog registers <c>KgsmUpnpInfo</c> afterward
/// (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableUpnpInfo : IUpnpInfo
{
    public Task<UpnpReading> GetForwardsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpnpReading(UpnpState.DaemonUnavailable, Array.Empty<UpnpForward>()));

    public Task<UpnpActionResult> OpenForwardsAsync(
        string instance, IReadOnlyList<PortRule> ports, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpnpActionResult(UpnpActionState.Unavailable, null));
}
