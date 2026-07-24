using System.Net;
using System.Net.Sockets;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// The logic behind <c>HttpWebFetch</c>'s <c>SocketsHttpHandler.ConnectCallback</c> (see
/// <c>ServiceCollectionExtensions.AddKgsmAdapters</c>), factored to take a plain
/// <see cref="DnsEndPoint"/> rather than the real callback's <c>SocketsHttpConnectionContext</c> — that
/// type's constructor is <c>internal</c> to <c>System.Net.Http</c>, so tests cannot build one; the DI
/// wiring's lambda is a one-line adapter (<c>(context, ct) =&gt; ConnectAsync(context.DnsEndPoint, ct)</c>)
/// that unpacks the only field this method needs. Factoring it out this way lets the SSRF-enforcement
/// wiring itself (not just <see cref="SsrfGuard"/> in isolation) be exercised directly in tests,
/// including the "reject before ever touching a socket" property.
/// <para>
/// The .NET HTTP stack re-invokes the real callback for EVERY connection it opens — the initial
/// request and each hop <c>HttpWebFetch</c>'s manual redirect loop issues alike (auto-redirect is
/// disabled specifically so this re-runs, and therefore re-validates, on every hop).
/// </para>
/// </summary>
internal static class SsrfConnectCallback
{
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var ip = await SsrfGuard.ResolveSafeAsync(endpoint.Host, cancellationToken);

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, endpoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
