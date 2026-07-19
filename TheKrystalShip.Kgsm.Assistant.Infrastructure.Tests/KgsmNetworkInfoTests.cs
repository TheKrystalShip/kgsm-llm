using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Exceptions;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the host-firewall adapter's mapping from kgsm-lib's <see cref="IFirewallService"/> result
/// types onto the assistant's neutral network shapes — no real socket. Verifies it preserves the honest
/// outcomes (unsupported / unknown / applied-inactive) and fails closed (never throws) when the authority
/// is unreachable (a thrown <see cref="FirewallException"/> → the unavailable outcome).
/// </summary>
public class KgsmNetworkInfoTests
{
    private readonly IFirewallService _firewall = Substitute.For<IFirewallService>();

    private KgsmNetworkInfo Create() => new(_firewall, NullLogger<KgsmNetworkInfo>.Instance);

    [Fact]
    public async Task GetPorts_MapsBackendEnforcementAndRules()
    {
        _firewall.ListOwnedAsync("factorio", Arg.Any<CancellationToken>())
            .Returns(new FirewallListResult
            {
                Status = FirewallListStatus.Ok,
                Enforcement = FirewallEnforcement.Enforcing,
                Rules = new[]
                {
                    new FirewallOwnedRule("factorio", new[]
                    {
                        new PortMapping { Start = 34197, End = 34197, Protocol = "udp" },
                    }),
                },
            });
        _firewall.BackendAsync(Arg.Any<CancellationToken>())
            .Returns(new FirewallBackendInfo { Backend = "ufw", CanApply = true, CanList = true });

        var reading = await Create().GetPortsAsync("factorio");

        reading.State.Should().Be(NetworkState.Available);
        reading.Backend.Should().Be("ufw");
        reading.ListState.Should().Be(PortListState.Enumerated);
        reading.Enforcement.Should().Be(NetworkEnforcement.Enforcing);
        reading.Ports.Should().ContainSingle()
            .Which.Should().Be(new PortRule(34197, 34197, "udp"));
    }

    [Fact]
    public async Task GetPorts_UnknownEnumeration_IsPreserved_NotEmptied()
    {
        _firewall.ListOwnedAsync("factorio", Arg.Any<CancellationToken>())
            .Returns(new FirewallListResult { Status = FirewallListStatus.Unknown });
        _firewall.BackendAsync(Arg.Any<CancellationToken>())
            .Returns(new FirewallBackendInfo { Backend = "ufw" });

        var reading = await Create().GetPortsAsync("factorio");

        reading.State.Should().Be(NetworkState.Available);
        reading.ListState.Should().Be(PortListState.Unknown); // honest unknown, not a fabricated empty
    }

    [Fact]
    public async Task GetPorts_AuthorityUnreachable_FailsClosed_NeverThrows()
    {
        _firewall.ListOwnedAsync("factorio", Arg.Any<CancellationToken>())
            .Returns<Task<FirewallListResult>>(_ => throw new FirewallException("down"));

        var reading = await Create().GetPortsAsync("factorio");

        reading.State.Should().Be(NetworkState.FirewallUnavailable);
        reading.Ports.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenPorts_MapsAppliedInactive_Outcome()
    {
        _firewall.EnsureOpenAsync("factorio", Arg.Any<IReadOnlyList<PortMapping>>(), Arg.Any<CancellationToken>())
            .Returns(new FirewallActionResult
            {
                Ok = true, Outcome = FirewallOutcome.AppliedInactive, Backend = "ufw", Detail = "ufw inactive",
            });

        var result = await Create().OpenPortsAsync("factorio", new[] { new PortRule(34197, 34197, "udp") });

        result.State.Should().Be(NetworkActionState.AppliedInactive);
        result.Backend.Should().Be("ufw");
        result.Detail.Should().Be("ufw inactive");
    }

    [Fact]
    public async Task OpenPorts_Unsupported_IsPreserved_NotFabricatedOpen()
    {
        _firewall.EnsureOpenAsync("factorio", Arg.Any<IReadOnlyList<PortMapping>>(), Arg.Any<CancellationToken>())
            .Returns(new FirewallActionResult { Ok = false, Outcome = FirewallOutcome.Unsupported, Backend = "none" });

        var result = await Create().OpenPortsAsync("factorio", new[] { new PortRule(34197, 34197, "udp") });

        result.State.Should().Be(NetworkActionState.Unsupported);
    }

    [Fact]
    public async Task OpenPorts_AuthorityUnreachable_FailsClosed_NeverThrows()
    {
        _firewall.EnsureOpenAsync("factorio", Arg.Any<IReadOnlyList<PortMapping>>(), Arg.Any<CancellationToken>())
            .Returns<Task<FirewallActionResult>>(_ => throw new FirewallException("down"));

        var result = await Create().OpenPortsAsync("factorio", new[] { new PortRule(34197, 34197, "udp") });

        result.State.Should().Be(NetworkActionState.FirewallUnavailable);
    }
}
