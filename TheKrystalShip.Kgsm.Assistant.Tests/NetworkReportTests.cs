using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic <c>get_network</c> composer is a pure function, so these run without mocks. They
/// pin the honesty rules across BOTH authorities: on the firewall axis an unavailable read is never
/// "nothing open", an unknown enumeration is never a fabricated empty set, an inactive backend is called
/// out; on the router/UPnP axis a queried-empty router is a real "no forwards", distinct from a router or
/// watchdog that couldn't be reached — neither of which is ever read as "nothing forwarded".
/// </summary>
public class NetworkReportTests
{
    private static NetworkReading Reading(
        NetworkState state = NetworkState.Available,
        string backend = "ufw",
        PortListState listState = PortListState.Enumerated,
        NetworkEnforcement enforcement = NetworkEnforcement.Enforcing,
        params PortRule[] ports) =>
        new(state, backend, listState, enforcement, ports);

    // Default router reading for firewall-focused tests: watchdog unreachable (an honest unknown that
    // doesn't collide with any firewall assertion).
    private static UpnpReading Upnp(
        UpnpState state = UpnpState.DaemonUnavailable, params UpnpForward[] forwards) =>
        new(state, forwards);

    [Fact]
    public void EnumeratedWithPorts_ListsThem_AndSubject()
    {
        var r = NetworkReport.Build(
            Reading(ports: new PortRule(34197, 34197, "udp")), Upnp(), "factorio");

        r.Tool.Should().Be(LlmTools.GetNetwork);
        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Subject.Should().Be(new ResultRef(ResourceKind.Network, "factorio"));
        r.Data.State.Should().Be(NetworkState.Available);
        r.Data.Ports.Should().ContainSingle();
        r.Summary.Should().Contain("34197/udp");
    }

    [Fact]
    public void EnumeratedEmpty_SaysNonePlain_NeverFabricated()
    {
        var r = NetworkReport.Build(Reading(), Upnp(), "terraria");

        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Summary.Should().Contain("no host-firewall ports");
    }

    [Fact]
    public void InactiveBackend_IsCalledOut_NotFiltering()
    {
        var r = NetworkReport.Build(
            Reading(enforcement: NetworkEnforcement.Inactive, ports: new PortRule(27015, 27015, "tcp")),
            Upnp(), "cs2");

        r.Summary.Should().Contain("not enforcing");
        r.Summary.Should().Contain("reachable");
        r.Data.Enforcement.Should().Be(NetworkEnforcement.Inactive);
    }

    [Fact]
    public void UnknownEnumeration_IsHonestUnknown_NotEmpty()
    {
        var r = NetworkReport.Build(Reading(listState: PortListState.Unknown), Upnp(), "valheim");

        // Honest unknown → only a Possible conclusion, never "none open".
        r.Confidence.Should().Be(Confidence.Possible);
        r.Summary.Should().Contain("unknown");
        r.Summary.Should().NotContain("no host-firewall ports");
    }

    [Fact]
    public void NoBackend_ReportsUnsupported_Honestly()
    {
        var r = NetworkReport.Build(
            Reading(backend: "none", listState: PortListState.Unsupported), Upnp(), "minecraft");

        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Summary.Should().Contain("No host firewall backend");
    }

    [Fact]
    public void FirewallUnavailable_IsCouldntRead_NotNothingOpen()
    {
        var r = NetworkReport.Build(
            Reading(state: NetworkState.FirewallUnavailable, backend: "", listState: PortListState.Unknown,
                enforcement: NetworkEnforcement.Unknown),
            Upnp(), "factorio");

        r.Confidence.Should().Be(Confidence.Possible);
        r.Data.State.Should().Be(NetworkState.FirewallUnavailable);
        r.Summary.Should().Contain("unavailable");
        r.Summary.Should().Contain("isn't a sign no ports are open");
    }

    // --- The router / UPnP axis -----------------------------------------------------------------

    [Fact]
    public void RouterQueriedWithForwards_ListsThem()
    {
        var r = NetworkReport.Build(
            Reading(),
            Upnp(UpnpState.Queried, new UpnpForward(8211, "udp", 8211, "192.168.1.10")),
            "palworld");

        r.Data.UpnpState.Should().Be(UpnpState.Queried);
        r.Data.Forwards.Should().ContainSingle();
        r.Summary.Should().Contain("router").And.Contain("8211/udp");
    }

    [Fact]
    public void RouterQueriedEmpty_IsRealNone_NotUnknown()
    {
        var r = NetworkReport.Build(Reading(), Upnp(UpnpState.Queried), "terraria");

        r.Data.UpnpState.Should().Be(UpnpState.Queried);
        r.Summary.Should().Contain("no port forwards");
    }

    [Fact]
    public void RouterUnavailable_IsCouldntAsk_NotNothingForwarded()
    {
        var r = NetworkReport.Build(Reading(), Upnp(UpnpState.RouterUnavailable), "factorio");

        r.Summary.Should().Contain("router couldn't be queried");
        r.Summary.Should().Contain("not a sign nothing is forwarded");
    }

    [Fact]
    public void DaemonUnavailable_IsRouterForwardingUnknown()
    {
        var r = NetworkReport.Build(Reading(), Upnp(UpnpState.DaemonUnavailable), "factorio");

        r.Summary.Should().Contain("Router/UPnP forwarding is unknown");
    }

    [Fact]
    public void NoSummaryImpliesUpnpIsUnobservable()
    {
        // The old firewall-only caveat is gone — UPnP IS now observed and reported.
        var r = NetworkReport.Build(Reading(), Upnp(UpnpState.Queried), "factorio");

        r.Summary.Should().NotContain("isn't observable from the host");
        r.Summary.Should().NotContain("not router/UPnP");
    }
}
