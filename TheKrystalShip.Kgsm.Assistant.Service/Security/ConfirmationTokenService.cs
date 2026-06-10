using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Mints and validates stateless, HMAC-signed confirmation tokens — the web analog of
/// the bot's Discord <c>customId</c>. The staged operation is encoded in the token and
/// signed, so a browser can round-trip it without a server-side store. The token is
/// authenticity + expiry only; it is NOT an authorization grant — the confirm endpoint
/// re-derives the caller's authority and <see cref="IServerAssistant.ConfirmAsync"/>
/// re-validates the target against live inventory (which also blunts replay within the TTL).
/// <para>
/// The token is bound to the Discord user who STAGED it (<c>U</c>); <c>/confirm</c> rejects
/// a token presented by a different principal, so one user can't confirm another's staged op.
/// </para>
/// </summary>
internal sealed class ConfirmationTokenService
{
    private readonly byte[] _key;
    private readonly int _ttlSeconds;

    public ConfirmationTokenService(IOptions<AssistantServiceOptions> options)
    {
        var confirmation = options.Value.Confirmation;
        _key = string.IsNullOrEmpty(confirmation.Key) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(confirmation.Key);
        _ttlSeconds = confirmation.TtlSeconds > 0 ? confirmation.TtlSeconds : 300;
    }

    /// <summary>True when a signing key is configured; without one, tokens can't be issued or trusted.</summary>
    public bool IsConfigured => _key.Length > 0;

    /// <param name="userId">The Discord user staging the op; the token is bound to them.</param>
    public string Create(PendingConfirmation confirmation, string userId)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Assistant:Confirmation:Key must be set to issue confirmation tokens.");

        var payload = new TokenPayload(
            (int)confirmation.Kind,
            confirmation.Target,
            confirmation.InstanceName,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _ttlSeconds,
            userId);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(_key, payloadBytes);
        return $"{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signature)}";
    }

    /// <param name="userId">The user the token was bound to at staging time (empty if absent).</param>
    public bool TryValidate(string? token, out PendingConfirmation confirmation, out string userId)
    {
        confirmation = null!;
        userId = string.Empty;
        if (!IsConfigured || string.IsNullOrWhiteSpace(token))
            return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return false;

        byte[] payloadBytes, providedSig;
        try
        {
            payloadBytes = Base64Url.Decode(token[..dot]);
            providedSig = Base64Url.Decode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSig = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
            return false;

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null)
            return false;

        if (payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false; // expired

        if (!Enum.IsDefined(typeof(ConfirmationKind), payload.K) || string.IsNullOrEmpty(payload.T))
            return false;

        confirmation = new PendingConfirmation((ConfirmationKind)payload.K, payload.T, payload.N);
        userId = payload.U ?? string.Empty;
        return true;
    }

    private sealed record TokenPayload(int K, string T, string? N, long Exp, string? U);
}
