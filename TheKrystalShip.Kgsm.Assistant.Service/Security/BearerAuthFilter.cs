using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter that authenticates a caller to the secured surface, two ways:
/// <list type="number">
/// <item><b>Session bearer</b> — <c>Authorization: Bearer &lt;token&gt;</c>, a session JWT this service
/// minted, resolved to an <see cref="AuthPrincipal"/> (the browser/SPA-direct path).</item>
/// <item><b>Member acting</b> — another member of this cluster calling for somebody who is not signed
/// in here, authenticated by its own member service token and naming the person on
/// <c>X-Kgsm-Acting</c>. There is no session login: the person is resolved from this member's own
/// replica of the cluster's accounts, and so is what they may do. The caller states who and never
/// what.</item>
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
    /// Key under which the tier this member holds for the person being acted for is stored (a
    /// <see cref="KgsmTier"/>), set ONLY on the member-acting path.
    /// </summary>
    /// <remarks>
    /// Read from this member's own replica of the cluster's accounts by the resolver, never from
    /// anything the caller sent. A caller states who it is acting for and nothing about what they may
    /// do, so a compromised member can act as somebody it names and never above what that person
    /// actually holds.
    /// <para>
    /// Absent on the session path, where the principal is a person who signed in and authority is
    /// re-derived from the same store at execution.
    /// </para>
    /// </remarks>
    public const string ActingTierKey = "actingTier";

    /// <summary>
    /// Key under which the acting person's per-turn AUTO-ACCEPT intent is stored (a <c>bool</c>), set
    /// ONLY on the member-acting path.
    /// </summary>
    /// <remarks>
    /// A preference and never a permission: it is ANDed with the tier this member resolved, so a caller
    /// cannot raise what it may do by asserting one. Absent or anything but <c>"true"</c> is
    /// propose-only, so a caller that does not speak this header can never grant anything by omission.
    /// </remarks>
    public const string ActingAutoActKey = "actingAutoAct";

    /// <summary>
    /// Key under which the trusted relay's per-CHAT conversation id is stored (a <c>string</c>), set
    /// ONLY on the member-acting path, from <c>X-Relay-Conversation-Id</c>. It is a SUB-scope of
    /// the forwarded user's memory namespace — the /turn handler keys memory as
    /// <c>web:{userId}[:{thisValue}]</c>, so it partitions one caller's own history into separate chats
    /// (each "new chat" in the SPA → a fresh context window) and can NEVER reach another user (the user
    /// id prefix is authoritative). Absent ⇒ not set ⇒ the bare per-user key (one conversation).
    /// </summary>
    public const string RelayConversationIdKey = "relayConversationId";

    /// <summary>
    /// Key under which the trusted relay's LEAF NAME is stored (a <c>string</c>), set ONLY on the
    /// member-acting path, from <c>X-Relay-Leaf</c>. It names the deployed leaf making the call
    /// (<c>kgsm-bot</c>, <c>kgsm-api</c>), and two things are derived from it: the prompt overrides
    /// that leaf's surface reads, and the audit origin its actions record under
    /// (<see cref="RelayLeaves"/>).
    /// <para>
    /// One header for both, because they answer the same question — <em>which surface is this?</em> —
    /// and two would let a caller claim one identity for its wording and another for the audit trail.
    /// Validated as a leaf name (<see cref="LeafName"/>) because it becomes a path segment; anything
    /// malformed is dropped, and a dropped or absent value reads the assistant's own prompts under its
    /// own origin. A relay that does not speak this header is therefore unchanged by it.
    /// </para>
    /// </summary>
    public const string RelayLeafKey = "relayLeaf";

    /// <summary>
    /// Key under which the trusted relay's ROOM is stored (a <c>string</c>), set ONLY on the
    /// member-acting path, ONLY from <c>X-Relay-Room</c>, and ONLY for a leaf
    /// <see cref="RelayLeaves.OpensRooms"/> permits. It names a conversation keyed to a PLACE — a
    /// Discord thread — which everyone speaking there shares, so the /turn handler keys memory as
    /// <c>room:{thisValue}</c> with no user segment at all.
    /// <para>
    /// This is the one conversation key on the service that is not prefixed with the caller's own
    /// verified id, so it is the one that cannot be made safe by construction. Three things stand in
    /// place of that: the relay secret (already matched to get here), the leaf allow-list, and the
    /// absence of any room path on the session-bearer side — a browser caller sending this header is
    /// answered exactly as one that did not, because the filter never reaches this branch for them.
    /// Absent, blank or from an unlisted leaf ⇒ not set ⇒ the ordinary per-user key.
    /// </para>
    /// </summary>
    public const string RelayRoomKey = "relayRoom";

    private const string BearerPrefix = "Bearer ";
    private const string RelayAutoActHeader = "X-Relay-Auto-Act";
    private const string RelayConversationIdHeader = "X-Relay-Conversation-Id";
    private const string RelayLeafHeader = "X-Relay-Leaf";
    private const string RelayRoomHeader = "X-Relay-Room";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly ISessionTokenService _tokens;
    private readonly ISessionValidator _sessions;
    private readonly UserDirectory _users;
    private readonly AssistantServiceOptions _options;
    private readonly IClusterSessionKeys _clusterKeys;
    private readonly ClusterSessionRevocations _clusterSessions;
    private readonly MemberActingResolver _acting;
    private readonly string _hostId;

    public BearerAuthFilter(
        ISessionTokenService tokens,
        ISessionValidator sessions,
        UserDirectory users,
        IOptions<AssistantServiceOptions> options,
        IOptions<AuthOptions> authOptions,
        IClusterSessionKeys clusterKeys,
        ClusterSessionRevocations clusterSessions,
        MemberActingResolver acting)
    {
        _tokens = tokens;
        _sessions = sessions;
        _users = users;
        _options = options.Value;
        _clusterKeys = clusterKeys;
        _clusterSessions = clusterSessions;
        _acting = acting;
        _hostId = authOptions.Value.ResolveHostId();
    }

    /// <summary>
    /// The rules a presented bearer is held to: this surface's own sessions, and the ones its
    /// cluster's auth anchor minted for every member.
    /// </summary>
    /// <remarks>
    /// Rebuilt per request rather than cached, because the anchor's published keys move — a rotation
    /// or a reassignment is meant to take effect without a restart anywhere, and a set captured once
    /// at startup would go on verifying against a key nobody signs with. The cost is assembling a
    /// parameters object; the keys behind it are already a snapshot the reader holds in memory.
    /// </remarks>
    private TokenValidationParameters Validation =>
        ClusterSessionValidation.Accepting(_tokens.ValidationParameters, _clusterKeys);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        // Member-acting path first, chosen by the acting header rather than by trying one scheme and
        // falling back: this and a session establish trust in completely different ways, and a fallback
        // would mean a failed member call quietly re-examined as somebody's session.
        string actingHandle = request.Headers[MemberActing.ActingHandleHeader].ToString();
        if (!string.IsNullOrWhiteSpace(actingHandle))
        {
            MemberActingResult acting = await _acting.ResolveAsync(
                actingHandle, ClusterRequest.ExtractBearerToken(request),
                context.HttpContext.RequestAborted);

            if (!acting.Succeeded)
                return Results.Unauthorized();

            KgsmUser person = acting.Person!;
            KgsmActor.TryParse(acting.Handle!, out string provider, out string subject);

            // No session of its own: the caller is a member, not a person signing in, so there is no
            // session id and a sign-out on this path has nothing to revoke. The provider comes from the
            // handle rather than being assumed — a member may act for somebody who signed in with a
            // password, and stamping them as a provider's would file them under an identity they do not
            // have.
            context.HttpContext.Items[PrincipalKey] = new AuthPrincipal(
                provider, subject, person.DisplayName, string.Empty,
                Owner: await OwnerKeys.ResolveAsync(
                    _users, provider, subject, context.HttpContext.RequestAborted));

            // The tier this member holds for them, read from its own replica by the resolver. It is
            // carried rather than re-read so that one call asks the question once, and it is the ONLY
            // source of authority on this path — nothing the caller sent contributes to it.
            context.HttpContext.Items[ActingTierKey] = person.Tier;

            // The person's own per-turn intent, and nothing more. It is ANDed with the tier this member
            // resolved, so a caller cannot raise what it may do by asserting a preference.
            context.HttpContext.Items[ActingAutoActKey] =
                string.Equals(request.Headers[RelayAutoActHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);

            // The per-chat conversation id — a SUB-scope of THIS person's memory. Stored raw; the
            // handler sanitises and caps it. Never cross-person: the resolved owner is the authoritative
            // prefix, so a caller naming somebody else's chat still gets their own.
            var relayConversationId = request.Headers[RelayConversationIdHeader].ToString();
            if (!string.IsNullOrWhiteSpace(relayConversationId))
                context.HttpContext.Items[RelayConversationIdKey] = relayConversationId;

            // The calling leaf, which selects its prompt overrides and its audit origin. Validated
            // rather than repaired: it is used as a path segment, and a name that has to be cleaned up
            // to be usable is a name this service should not act on.
            string? leaf = LeafName.Validate(request.Headers[RelayLeafHeader].ToString());
            if (leaf is not null)
                context.HttpContext.Items[RelayLeafKey] = leaf;

            // The room, read LAST because it is the one header whose meaning depends on another: only a
            // leaf on the room allow-list may name a conversation that is not prefixed with the acting
            // person's own id. An unlisted leaf is not an error — its request is simply the per-person
            // one it would have been without the header.
            var relayRoom = request.Headers[RelayRoomHeader].ToString();
            if (!string.IsNullOrWhiteSpace(relayRoom) && RelayLeaves.OpensRooms(leaf))
                context.HttpContext.Items[RelayRoomKey] = relayRoom;

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
        TokenValidationResult result = await Handler.ValidateTokenAsync(token, Validation);
        if (!result.IsValid || result.ClaimsIdentity is null)
            return null;

        ClaimsIdentity ci = result.ClaimsIdentity;

        if (ci.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Access)
            return null;

        KgsmIdentity? identity = SessionClaims.ReadIdentity(ci);
        string? sessionId = SessionClaims.ReadSessionId(ci);
        if (identity is null || sessionId is null)
            return null;

        // Two kinds of session, held to opposite questions about the same table.
        //
        // One this member minted has a row, so the row IS the session: no live row means no session,
        // and the check is an allow-list. One the cluster's auth anchor minted has no row here — the
        // sign-in happened on another machine — and is accepted because its signature verifies against
        // the key that member publishes. There is nothing to look up, so the only thing worth storing
        // is that somebody ended it, and the check is a deny-list. Running the allow-list against a
        // cluster session would refuse every one of them, which is "sign in once" failing everywhere
        // but the anchor.
        if (ClusterSessionValidation.IsClusterSession(ci, _hostId))
        {
            if (await _clusterSessions.IsRevokedAsync(sessionId, ct))
                return null;
        }
        else if (!await _sessions.IsValidAsync(sessionId, ct))
        {
            return null;
        }

        // A switched-off account is a door closing, not a demotion. Left merely tierless it would keep
        // reading its own conversations and holding an event stream open, so the session ends here —
        // which is what makes disabling someone in the Control Panel cut their live sessions on this
        // surface too, with no call between the two services.
        //
        // A store that cannot be read leaves the session standing. Every authority question this
        // request goes on to ask reports the outage on its own terms, and ending sessions on a
        // momentary read failure would sign the whole host out over a locked file.
        if (_users.Available)
        {
            try
            {
                if ((await _users.Authority!.ResolveAsync(identity, ct)).Outcome == AuthorityOutcome.Disabled)
                    return null;
            }
            catch (KgsmAuthProviderException)
            {
                // Unanswerable is not "no".
            }
        }

        // What proves them is the subject; what their things are stored under is the account it
        // belongs to. Resolved here, once, so no handler has to remember the difference.
        return new AuthPrincipal(
            identity.Provider, identity.Subject, identity.Display, sessionId, SessionClaims.ReadTier(ci),
            Owner: await OwnerKeys.ResolveAsync(_users, identity.Provider, identity.Subject, ct));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
