using System.Net;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the SSRF guard's address classification and resolution in isolation from the HTTP
/// transport — pure, deterministic, no network involved for the blocked cases (a literal IP is
/// classified without any DNS lookup, and <see cref="SsrfGuard.ResolveSafeAsync"/> throws before a
/// socket is ever created). This is the safety-load-bearing piece for <c>fetch_url</c>: this host is
/// internet-exposed and the fetched URL is model/user-influenced.
/// </summary>
public class SsrfGuardTests
{
    [Theory]
    // Loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    // RFC 1918 private ranges
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    // Link-local, INCLUDING the cloud-metadata address
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    // Unspecified / "this network"
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    // Multicast
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    // IPv6 unique-local
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    // Carrier-grade NAT + reserved
    [InlineData("100.64.0.1")]
    [InlineData("255.255.255.255")]
    // IPv4-mapped IPv6 wrapping a blocked address
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void IsBlocked_TrueForPrivateLoopbackLinkLocalMulticastAndReserved(string ip)
    {
        SsrfGuard.IsBlocked(IPAddress.Parse(ip)).Should().BeTrue($"{ip} must never be reachable from fetch_url");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void IsBlocked_FalseForOrdinaryPublicAddresses(string ip)
    {
        SsrfGuard.IsBlocked(IPAddress.Parse(ip)).Should().BeFalse($"{ip} is a public address fetch_url should be allowed to reach");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("169.254.169.254")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.1")]
    public async Task ResolveSafeAsync_BlockedHost_ThrowsSsrfBlockedException(string host)
    {
        var act = () => SsrfGuard.ResolveSafeAsync(host, CancellationToken.None);

        await act.Should().ThrowAsync<SsrfBlockedException>(
            $"'{host}' resolves to a loopback/private/link-local address fetch_url must refuse");
    }

    [Fact]
    public async Task ResolveSafeAsync_LiteralPublicAddress_ReturnsItWithoutDns()
    {
        // A literal IP is classified directly (IPAddress.TryParse), never through Dns.* — so this
        // assertion holds even with no network reachability in the test environment.
        var result = await SsrfGuard.ResolveSafeAsync("8.8.8.8", CancellationToken.None);

        result.Should().Be(IPAddress.Parse("8.8.8.8"));
    }

    [Fact]
    public async Task ResolveSafeAsync_UnresolvableHost_ThrowsSsrfBlockedException()
    {
        // Any DNS failure (NXDOMAIN, or no resolver reachable at all in a sandboxed test run) is
        // mapped to the same fail-closed outcome — never silently treated as "safe by default".
        var act = () => SsrfGuard.ResolveSafeAsync("this-host-should-not-resolve.invalid", CancellationToken.None);

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }
}
