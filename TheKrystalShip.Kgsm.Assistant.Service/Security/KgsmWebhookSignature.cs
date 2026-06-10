using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Verifies the <c>X-KGSM-Signature</c> header kgsm sends on webhook events. Contract
/// (from kgsm <c>modules/events.webhook.sh</c>): the header is
/// <c>sha256=&lt;base64(HMAC-SHA256(rawBody, webhook_secret))&gt;</c>, signing the exact
/// raw request body bytes. We compare with a constant-time check.
/// </summary>
internal static class KgsmWebhookSignature
{
    private const string Prefix = "sha256=";

    /// <summary>
    /// True when <paramref name="header"/> is a valid signature of <paramref name="body"/>
    /// under <paramref name="secret"/>. False on a missing/malformed header or mismatch.
    /// </summary>
    public static bool Verify(string? header, ReadOnlySpan<byte> body, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header))
            return false;

        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(header[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32]; // HMAC-SHA256 = 32 bytes
        var written = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);

        return CryptographicOperations.FixedTimeEquals(expected[..written], provided);
    }
}
