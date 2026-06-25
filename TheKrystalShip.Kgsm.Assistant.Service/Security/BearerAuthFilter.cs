using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter that authenticates a caller to the secured surface, two ways:
/// <list type="number">
/// <item><b>Session bearer</b> — <c>Authorization: Bearer &lt;token&gt;</c> resolved to an
/// <see cref="AuthPrincipal"/> (the browser/SPA-direct path).</item>
/// <item><b>Trusted relay</b> — a co-located kgsm-api forwarding a verified end-user. A request
/// carrying a matching <c>X-Relay-Secret</c> is authenticated as the forwarded Discord identity
/// (<c>X-Relay-User</c> + optional <c>X-Relay-User-Name</c>) with NO session login. The relay
/// forwards identity only; authority is still derived from the bot by user id downstream. Enabled
/// only when <see cref="RelayOptions.Secret"/> is configured.</item>
/// </list>
/// Either way the resolved principal is stashed on <see cref="HttpContext.Items"/> for the handler;
/// a missing/unresolvable credential short-circuits with a clean 401 (never calling the handler).
/// <para>
/// Hand-rolled rather than framework <c>AddAuthentication</c>: matches this service's other
/// explicit security primitives and gives a clean 401 (a <c>BindAsync</c>-null principal
/// would yield 400 instead). CORS preflight is answered by the CORS middleware before this
/// filter runs, so cross-origin OPTIONS stays unauthenticated.
/// </para>
/// </summary>
internal sealed class BearerAuthFilter : IEndpointFilter
{
    /// <summary>Key under which the resolved <see cref="AuthPrincipal"/> is stored on the request.</summary>
    public const string PrincipalKey = "principal";

    /// <summary>
    /// Key under which the trusted relay's action-authority decision is stored (a <c>bool</c>), set
    /// ONLY on the authenticated relay path. The api forwards its verified tier decision (operator+
    /// AND the user's per-turn toggle) as <c>X-Relay-Can-Act</c>; the /turn handler trusts it instead
    /// of a Discord role lookup. Absent ⇒ the session-bearer path (authority comes from Discord).
    /// </summary>
    public const string RelayCanActKey = "relayCanAct";

    private const string BearerPrefix = "Bearer ";
    private const string RelaySecretHeader = "X-Relay-Secret";
    private const string RelayUserHeader = "X-Relay-User";
    private const string RelayUserNameHeader = "X-Relay-User-Name";
    private const string RelayCanActHeader = "X-Relay-Can-Act";

    private readonly DiscordAuthService _auth;
    private readonly AssistantServiceOptions _options;

    public BearerAuthFilter(DiscordAuthService auth, IOptions<AssistantServiceOptions> options)
    {
        _auth = auth;
        _options = options.Value;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        // Trusted-relay path first: only when a relay secret is configured AND the caller presents
        // one. A present-but-wrong secret is a hard 401 (a misconfigured/forged relay), never a
        // silent fall-through to the session path.
        var relaySecret = _options.Relay.Secret;
        var presentedSecret = request.Headers[RelaySecretHeader].ToString();
        if (!string.IsNullOrEmpty(relaySecret) && !string.IsNullOrEmpty(presentedSecret))
        {
            if (!FixedTimeEquals(presentedSecret, relaySecret))
                return Results.Unauthorized();

            var userId = request.Headers[RelayUserHeader].ToString();
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized(); // the relay MUST forward an identity to act as

            var displayName = request.Headers[RelayUserNameHeader].ToString();
            // SessionToken is a synthetic marker — the relay path has no session (logout is a no-op).
            context.HttpContext.Items[PrincipalKey] = new AuthPrincipal(
                userId, string.IsNullOrWhiteSpace(displayName) ? userId : displayName, "relay");
            // The relay's action-authority decision (the api's verified tier ∧ the user's toggle).
            // Trusted only because the relay secret already matched. Absent/anything-but-"true" ⇒ false,
            // so a relay that doesn't speak this header can never silently grant actions.
            context.HttpContext.Items[RelayCanActKey] =
                string.Equals(request.Headers[RelayCanActHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            return await next(context);
        }

        // Session-bearer path (the SPA-direct / browser caller).
        var header = request.Headers.Authorization.ToString();

        string? token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(token) || !_auth.TryResolvePrincipal(token, out var principal))
            return Results.Unauthorized();

        context.HttpContext.Items[PrincipalKey] = principal;
        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
