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
/// Router / UPnP forwarding visibility for one instance — the neutral capability that lets
/// <c>get_network</c> observe an instance's router forwards. Read-only: the forwards are opened by the
/// supervisor when an instance starts and released when it stops, so there is no forward for this leaf
/// to drive. A leaf capability that reaches the kgsm-watchdog (the sole owner of UPnP forwarding, via
/// its control socket); the host supplies the adapter. Separate authority from the host firewall
/// (<see cref="INetworkInfo"/>) — never conflated. Additive and fails closed: with no watchdog reachable
/// a read returns a <see cref="UpnpState.DaemonUnavailable"/> result, so the assistant composes and
/// boots standalone. Implementations MUST NOT throw — every failure maps to an unavailable outcome.
/// </summary>
public interface IUpnpInfo
{
    /// <summary>Read the router / UPnP forwards the IGD owns for one instance (tagged with its name).</summary>
    Task<UpnpReading> GetForwardsAsync(string instance, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IUpnpInfo"/> for hosts that have not wired a watchdog client: every read fails
/// closed as <see cref="UpnpState.DaemonUnavailable"/>, so embedding the assistant library never breaks
/// DI just because the watchdog isn't configured. <c>AddKgsmAssistant</c> registers this with
/// <c>TryAddSingleton</c>; a host that wires the watchdog registers <c>KgsmUpnpInfo</c> afterward
/// (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableUpnpInfo : IUpnpInfo
{
    public Task<UpnpReading> GetForwardsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpnpReading(UpnpState.DaemonUnavailable, Array.Empty<UpnpForward>()));
}
