using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic <c>get_network</c> composer is a pure function, so these run without mocks. They
/// pin the honesty rules: an unavailable firewall is never "nothing open", an unknown enumeration is
/// never a fabricated empty set, an inactive backend is called out, and EVERY summary states it covers
/// the host firewall only (never router/UPnP).
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

    [Fact]
    public void EnumeratedWithPorts_ListsThem_AndSubject()
    {
        var r = NetworkReport.Build(
            Reading(ports: new PortRule(34197, 34197, "udp")), "factorio");

        r.Tool.Should().Be(LlmTools.GetNetwork);
        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Subject.Should().Be(new ResultRef(ResourceKind.Network, "factorio"));
        r.Data.State.Should().Be(NetworkState.Available);
        r.Data.Ports.Should().ContainSingle();
        r.Summary.Should().Contain("34197/udp");
        r.Summary.Should().Contain("host firewall only");
    }

    [Fact]
    public void EnumeratedEmpty_SaysNonePlain_NeverFabricated()
    {
        var r = NetworkReport.Build(Reading(), "terraria");

        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Summary.Should().Contain("no host-firewall ports");
        r.Summary.Should().Contain("host firewall only");
    }

    [Fact]
    public void InactiveBackend_IsCalledOut_NotFiltering()
    {
        var r = NetworkReport.Build(
            Reading(enforcement: NetworkEnforcement.Inactive, ports: new PortRule(27015, 27015, "tcp")),
            "cs2");

        r.Summary.Should().Contain("not enforcing");
        r.Summary.Should().Contain("reachable");
        r.Data.Enforcement.Should().Be(NetworkEnforcement.Inactive);
    }

    [Fact]
    public void UnknownEnumeration_IsHonestUnknown_NotEmpty()
    {
        var r = NetworkReport.Build(Reading(listState: PortListState.Unknown), "valheim");

        // Honest unknown → only a Possible conclusion, never "none open".
        r.Confidence.Should().Be(Confidence.Possible);
        r.Summary.Should().Contain("unknown");
        r.Summary.Should().NotContain("no host-firewall ports");
    }

    [Fact]
    public void NoBackend_ReportsUnsupported_Honestly()
    {
        var r = NetworkReport.Build(
            Reading(backend: "none", listState: PortListState.Unsupported), "minecraft");

        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Summary.Should().Contain("No host firewall backend");
        r.Summary.Should().Contain("host firewall only");
    }

    [Fact]
    public void FirewallUnavailable_IsCouldntRead_NotNothingOpen()
    {
        var r = NetworkReport.Build(
            Reading(state: NetworkState.FirewallUnavailable, backend: "", listState: PortListState.Unknown,
                enforcement: NetworkEnforcement.Unknown),
            "factorio");

        r.Confidence.Should().Be(Confidence.Possible);
        r.Data.State.Should().Be(NetworkState.FirewallUnavailable);
        r.Summary.Should().Contain("unavailable");
        r.Summary.Should().Contain("isn't a sign no ports are open");
        r.Summary.Should().Contain("host firewall only");
    }
}
