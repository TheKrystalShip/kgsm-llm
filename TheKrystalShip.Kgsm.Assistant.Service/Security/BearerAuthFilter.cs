using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter that authenticates a caller to the secured surface, two ways:
/// <list type="number">
/// <item><b>Session bearer</b> — <c>Authorization: Bearer &lt;token&gt;</c>, a session JWT this service
/// minted, resolved to an <see cref="AuthPrincipal"/> (the browser/SPA-direct path).</item>
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
    /// ONLY on the authenticated relay path. The api forwards its verified tier decision (operator+,
    /// the authority to PROPOSE) as <c>X-Relay-Can-Act</c>; the /turn handler trusts it instead
    /// of a Discord role lookup. Absent ⇒ the session-bearer path (authority comes from Discord).
    /// </summary>
    public const string RelayCanActKey = "relayCanAct";

    /// <summary>
    /// Key under which the trusted relay's AUTO-ACCEPT decision is stored (a <c>bool</c>), set ONLY on
    /// the authenticated relay path. The api forwards its verified <em>admin</em>-tier ∧ per-turn
    /// toggle decision as <c>X-Relay-Auto-Act</c>; when true the /turn handler lets the dispatcher run
    /// lifecycle commands immediately instead of staging them. Strictly stronger than
    /// <see cref="RelayCanActKey"/>. Absent/non-"true" ⇒ false (propose-only), so a relay that doesn't
    /// speak this header can never silently auto-execute.
    /// </summary>
    public const string RelayAutoActKey = "relayAutoAct";

    /// <summary>
    /// Key under which the trusted relay's ADMIN decision is stored (a <c>bool</c>), set ONLY on the
    /// authenticated relay path from <c>X-Relay-Admin</c>. The api forwards its verified admin-tier
    /// decision; the review endpoints trust it instead of a Discord role lookup, exactly as
    /// <see cref="RelayCanActKey"/> works for actions. Absent/non-"true" ⇒ false, so a relay that
    /// doesn't speak this header can never open the review surface.
    /// </summary>
    public const string RelayAdminKey = "relayAdmin";

    /// <summary>
    /// Key under which the trusted relay's per-CHAT conversation id is stored (a <c>string</c>), set
    /// ONLY on the authenticated relay path from <c>X-Relay-Conversation-Id</c>. It is a SUB-scope of
    /// the forwarded user's memory namespace — the /turn handler keys memory as
    /// <c>web:{userId}[:{thisValue}]</c>, so it partitions one caller's own history into separate chats
    /// (each "new chat" in the SPA → a fresh context window) and can NEVER reach another user (the user
    /// id prefix is authoritative). Absent ⇒ not set ⇒ the bare per-user key (one conversation).
    /// </summary>
    public const string RelayConversationIdKey = "relayConversationId";

    private const string BearerPrefix = "Bearer ";
    private const string RelaySecretHeader = "X-Relay-Secret";
    private const string RelayUserHeader = "X-Relay-User";
    private const string RelayUserNameHeader = "X-Relay-User-Name";
    private const string RelayCanActHeader = "X-Relay-Can-Act";
    private const string RelayAutoActHeader = "X-Relay-Auto-Act";
    private const string RelayAdminHeader = "X-Relay-Admin";
    private const string RelayConversationIdHeader = "X-Relay-Conversation-Id";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly ISessionTokenService _tokens;
    private readonly ISessionValidator _sessions;
    private readonly AssistantServiceOptions _options;

    public BearerAuthFilter(
        ISessionTokenService tokens,
        ISessionValidator sessions,
        IOptions<AssistantServiceOptions> options)
    {
        _tokens = tokens;
        _sessions = sessions;
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
            // The relay path holds no session of its own — the api authenticated the user upstream —
            // so the session id is empty and a logout on this path has nothing to revoke.
            context.HttpContext.Items[PrincipalKey] = new AuthPrincipal(
                userId, string.IsNullOrWhiteSpace(displayName) ? userId : displayName, string.Empty);
            // The relay's action-authority decision (the api's verified tier ∧ the user's toggle).
            // Trusted only because the relay secret already matched. Absent/anything-but-"true" ⇒ false,
            // so a relay that doesn't speak this header can never silently grant actions.
            context.HttpContext.Items[RelayCanActKey] =
                string.Equals(request.Headers[RelayCanActHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            // The api's auto-accept decision (its verified admin-tier ∧ toggle). Same trust basis (the
            // secret already matched) and same fail-closed default — anything but "true" ⇒ propose-only.
            context.HttpContext.Items[RelayAutoActKey] =
                string.Equals(request.Headers[RelayAutoActHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            // The api's admin decision, for the review surface. Same trust basis (the secret matched)
            // and same fail-closed default — anything but "true" leaves the surface shut.
            context.HttpContext.Items[RelayAdminKey] =
                string.Equals(request.Headers[RelayAdminHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            // The per-chat conversation id — a SUB-scope of THIS user's memory (the handler keys
            // web:{userId}[:{id}]). Stored raw; the handler sanitises + caps it. Never cross-user: the
            // user id is the authoritative prefix. Absent ⇒ unset ⇒ the handler uses the bare per-user
            // key, so an older api/relay that doesn't send it stays single-context (unchanged behaviour).
            var relayConversationId = request.Headers[RelayConversationIdHeader].ToString();
            if (!string.IsNullOrWhiteSpace(relayConversationId))
                context.HttpContext.Items[RelayConversationIdKey] = relayConversationId;
            return await next(context);
        }

        // Session-bearer path (the browser caller).
        var header = request.Headers.Authorization.ToString();

        string? token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        AuthPrincipal? principal = await ResolveAsync(token, context.HttpContext.RequestAborted);
        if (principal is null)
            return Results.Unauthorized();

        context.HttpContext.Items[PrincipalKey] = principal;
        return await next(context);
    }

    /// <summary>
    /// Validates an access token and confirms its session is still alive.
    /// </summary>
    /// <remarks>
    /// Signature, issuer, audience and lifetime come from the same
    /// <see cref="ISessionTokenService.ValidationParameters"/> the mint used, so the check can never
    /// drift from the issue. Three things beyond that are refused outright: a <em>refresh</em> token
    /// presented as a bearer (it lives far longer, and accepting one here would erase the short access
    /// lifetime that bounds privilege), a token carrying no <c>sid</c> (nothing a revoke could kill),
    /// and a <c>sid</c> whose session is revoked or past its cap — the last is what makes signing out
    /// mean something, since a signed token stays cryptographically valid until it expires.
    /// </remarks>
    private async Task<AuthPrincipal?> ResolveAsync(string token, CancellationToken ct)
    {
        TokenValidationResult result = await Handler.ValidateTokenAsync(token, _tokens.ValidationParameters);
        if (!result.IsValid || result.ClaimsIdentity is null)
            return null;

        ClaimsIdentity ci = result.ClaimsIdentity;

        if (ci.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Access)
            return null;

        DiscordIdentity? identity = SessionClaims.ReadIdentity(ci);
        string? sessionId = SessionClaims.ReadSessionId(ci);
        if (identity is null || sessionId is null)
            return null;

        if (!await _sessions.IsValidAsync(sessionId, ct))
            return null;

        return new AuthPrincipal(identity.UserId, identity.Display, sessionId, SessionClaims.ReadTier(ci));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
