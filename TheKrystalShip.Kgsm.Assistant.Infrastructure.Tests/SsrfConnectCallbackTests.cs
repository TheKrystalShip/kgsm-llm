using System.Net;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Exercises the logic behind <c>HttpWebFetch</c>'s <c>SocketsHttpHandler.ConnectCallback</c>
/// (<see cref="SsrfConnectCallback.ConnectAsync"/>) — not just <see cref="SsrfGuard"/> in isolation —
/// so the production wiring itself is proven to reject a blocked target BEFORE any socket is opened.
/// (The real callback's <c>SocketsHttpConnectionContext</c> has an <c>internal</c> constructor and
/// can't be built here; the DI wiring's lambda is a one-line unwrap to the <see cref="DnsEndPoint"/>
/// this method actually needs — see <see cref="SsrfConnectCallback"/>'s doc comment.) No real HTTP
/// round trip or live network is needed: for a blocked host the guard throws inside
/// <c>SsrfGuard.ResolveSafeAsync</c> before a <see cref="System.Net.Sockets.Socket"/> is ever created.
/// <para>
/// Because .NET's HttpClient re-invokes the real callback for every connection it opens — the initial
/// request AND each hop of a manually-followed redirect alike (auto-redirect is off specifically so
/// this re-runs) — proving it rejects a blocked target here, together with
/// <c>HttpWebFetchTests</c>'s proof that the adapter issues one <see cref="HttpClient.SendAsync"/> per
/// redirect hop over that same handler, together demonstrate the "re-validated on every hop" property
/// without needing a live end-to-end redirect chain (which would itself have to bounce through a
/// non-loopback address to avoid tripping the very guard under test).
/// </para>
/// </summary>
public class SsrfConnectCallbackTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("localhost")]
    public async Task ConnectAsync_BlockedTarget_ThrowsWithoutOpeningASocket(string host)
    {
        var act = () => SsrfConnectCallback.ConnectAsync(new DnsEndPoint(host, 443), CancellationToken.None).AsTask();

        // SsrfBlockedException is thrown from inside SsrfGuard.ResolveSafeAsync, strictly before the
        // Socket/ConnectAsync call in SsrfConnectCallback — so a blocked host never reaches the network.
        await act.Should().ThrowAsync<SsrfBlockedException>();
    }
}
