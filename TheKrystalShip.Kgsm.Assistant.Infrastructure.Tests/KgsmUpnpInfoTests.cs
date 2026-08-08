using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the router/UPnP adapter's mapping from kgsm-lib's <see cref="IWatchdogClient"/> result
/// types onto the assistant's neutral UPnP shapes — no real socket. Verifies the two honest-unknown
/// layers are preserved (daemon-null vs in-body "unavailable" vs a real "queried") and that it fails
/// closed (never throws) when the watchdog is unreachable.
/// </summary>
public class KgsmUpnpInfoTests
{
    private readonly IWatchdogClient _watchdog = Substitute.For<IWatchdogClient>();

    private KgsmUpnpInfo Create() => new(_watchdog, NullLogger<KgsmUpnpInfo>.Instance);

    [Fact]
    public async Task GetForwards_Queried_MapsMappings()
    {
        _watchdog.GetUpnpAsync("factorio", Arg.Any<CancellationToken>())
            .Returns(new WatchdogUpnpList
            {
                Instance = "factorio",
                State = "queried",
                Mappings = new List<WatchdogUpnpMapping>
                {
                    new() { ExternalPort = 34197, Protocol = "udp", InternalPort = 34197, InternalClient = "192.168.1.10", Description = "factorio" },
                },
            });

        var reading = await Create().GetForwardsAsync("factorio");

        reading.State.Should().Be(UpnpState.Queried);
        reading.Forwards.Should().ContainSingle()
            .Which.Should().Be(new UpnpForward(34197, "udp", 34197, "192.168.1.10"));
    }

    [Fact]
    public async Task GetForwards_QueriedEmpty_IsRealNone_NotUnknown()
    {
        _watchdog.GetUpnpAsync("factorio", Arg.Any<CancellationToken>())
            .Returns(new WatchdogUpnpList { Instance = "factorio", State = "queried", Mappings = new() });

        var reading = await Create().GetForwardsAsync("factorio");

        reading.State.Should().Be(UpnpState.Queried); // a real "none", not RouterUnavailable
        reading.Forwards.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForwards_RouterUnavailable_IsPreserved_NotQueried()
    {
        // Daemon reached, router not — the in-body "unavailable" must NOT collapse to a queried-empty.
        _watchdog.GetUpnpAsync("factorio", Arg.Any<CancellationToken>())
            .Returns(new WatchdogUpnpList { Instance = "factorio", State = "unavailable", Mappings = new() });

        var reading = await Create().GetForwardsAsync("factorio");

        reading.State.Should().Be(UpnpState.RouterUnavailable);
        reading.Forwards.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForwards_DaemonNull_IsDaemonUnavailable()
    {
        _watchdog.GetUpnpAsync("factorio", Arg.Any<CancellationToken>())
            .Returns((WatchdogUpnpList?)null);

        var reading = await Create().GetForwardsAsync("factorio");

        reading.State.Should().Be(UpnpState.DaemonUnavailable);
    }

    [Fact]
    public async Task GetForwards_Throws_FailsClosed_NeverThrows()
    {
        _watchdog.GetUpnpAsync("factorio", Arg.Any<CancellationToken>())
            .Returns<Task<WatchdogUpnpList?>>(_ => throw new HttpRequestException("down"));

        var reading = await Create().GetForwardsAsync("factorio");

        reading.State.Should().Be(UpnpState.DaemonUnavailable);
    }

}
