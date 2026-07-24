using System.Net;
using System.Net.Sockets;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// Thrown by <see cref="SsrfGuard.ResolveSafeAsync"/> (surfaced through <c>HttpWebFetch</c>'s
/// <c>ConnectCallback</c>) when a host resolves only to addresses <see cref="SsrfGuard.IsBlocked"/>
/// refuses, or fails to resolve at all. Internal: it never crosses the <c>IWebFetch</c> port boundary
/// as a real exception — the adapter catches it and maps it to an honest <c>Result.Failure</c>.
/// </summary>
internal sealed class SsrfBlockedException(string message) : Exception(message);

/// <summary>
/// The SSRF guard for <c>fetch_url</c>: this host is internet-exposed and the fetched URL is
/// model/user-influenced, so a naive HTTP client would let it pivot to internal services (the local
/// kgsm-api, the monitor's unix-socket peers, ollama on localhost, cloud metadata endpoints). Every
/// resolved address is classified — loopback, RFC1918 private, link-local (which also covers the
/// 169.254.169.254 cloud-metadata address), multicast, unspecified, CGNAT, and reserved ranges are all
/// refused.
/// <para>
/// This is applied on EVERY connection attempt — the initial host and every redirect hop alike — by
/// <c>HttpWebFetch</c>'s <c>SocketsHttpHandler.ConnectCallback</c> (auto-redirect is disabled there;
/// the adapter follows redirects manually so each hop re-enters this guard). Resolving the hostname
/// here and connecting to the EXACT address just validated (rather than letting a later layer
/// re-resolve it) is what defeats a DNS-rebinding attack: a second lookup made after this check could
/// return a different, unsafe address, but nothing downstream ever gets the chance to look again.
/// </para>
/// </summary>
internal static class SsrfGuard
{
    /// <summary>True when <paramref name="ip"/> is inside a range <c>fetch_url</c> must never reach.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return IsBlocked(ip.MapToIPv4());

            var b = ip.GetAddressBytes();
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true; // fe80::/10 link-local
            if ((b[0] & 0xfe) == 0xfc) return true;                 // fc00::/7 unique-local
            if (b[0] == 0xff) return true;                          // ff00::/8 multicast
            return false;
        }

        var v4 = ip.GetAddressBytes();
        if (v4[0] == 10) return true;                                // 10.0.0.0/8
        if (v4[0] == 172 && v4[1] is >= 16 and <= 31) return true;    // 172.16.0.0/12
        if (v4[0] == 192 && v4[1] == 168) return true;                // 192.168.0.0/16
        if (v4[0] == 169 && v4[1] == 254) return true;                // 169.254.0.0/16 (link-local; covers the 169.254.169.254 cloud-metadata address)
        if (v4[0] == 127) return true;                                // 127.0.0.0/8 loopback
        if (v4[0] == 0) return true;                                  // 0.0.0.0/8 "this network"
        if (v4[0] == 100 && v4[1] is >= 64 and <= 127) return true;   // 100.64.0.0/10 carrier-grade NAT
        if (v4[0] is >= 224 and <= 239) return true;                  // 224.0.0.0/4 multicast
        if (v4[0] >= 240) return true;                                // 240.0.0.0/4 reserved + broadcast
        return false;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> (a hostname, or a literal IPv4/IPv6 address) and returns the
    /// first candidate address that <see cref="IsBlocked"/> allows. Throws
    /// <see cref="SsrfBlockedException"/> when the host is a literal blocked address, resolution
    /// fails outright, or every resolved candidate is blocked — never silently returns an unsafe
    /// address.
    /// </summary>
    public static async Task<IPAddress> ResolveSafeAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsBlocked(literal))
                throw new SsrfBlockedException($"'{host}' is a private/internal address");
            return literal;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SsrfBlockedException($"'{host}' could not be resolved ({ex.Message})");
        }

        var safe = Array.Find(addresses, a => !IsBlocked(a));
        if (safe is null)
            throw new SsrfBlockedException(
                addresses.Length == 0
                    ? $"'{host}' did not resolve to any address"
                    : $"'{host}' resolves only to private/internal addresses");

        return safe;
    }
}
