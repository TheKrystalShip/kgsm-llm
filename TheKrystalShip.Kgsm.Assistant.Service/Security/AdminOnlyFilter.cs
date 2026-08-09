using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter gating the review surface — the endpoints that read OTHER users' conversations.
/// Runs after <see cref="BearerAuthFilter"/> (which has already established WHO the caller is) and
/// answers only WHETHER they may review, from whichever path authenticated them:
/// <list type="bullet">
/// <item><b>Trusted relay:</b> the caller's verified tier, forwarded as <c>X-Relay-Tier</c> and stashed
/// as <see cref="BearerAuthFilter.RelayTierKey"/>. Trusted because the relay secret already matched.</item>
/// <item><b>Session bearer:</b> the caller's own Discord review role
/// (<see cref="AuthService.IsAdminAsync"/>), so the leaf's review surface works standalone,
/// with no api in front of it. No configured review role ⇒ nobody.</item>
/// </list>
/// Fail-closed on both paths: an unauthenticated request never reaches here (401 already), and an
/// authenticated non-admin gets a clean 403 without the handler running.
/// <para>
/// A third outcome is reported apart from those two: when Discord cannot be asked what the caller
/// holds, the gate answers <c>502</c> with <c>authority_unavailable</c> rather than <c>403</c>. Access
/// is refused either way — nobody is admitted during an outage — but a client that is told "denied"
/// shows the operator a permissions problem to go and investigate, when what happened is that an
/// upstream was briefly down and the next request will succeed.
/// </para>
/// </summary>
internal sealed class AdminOnlyFilter : IEndpointFilter
{
    // A plain 403, not Results.Forbid(): this service authenticates by hand and registers no
    // authentication scheme, so Forbid() — which asks a scheme to write the challenge — throws and
    // surfaces as a 500. Same reason BearerAuthFilter returns Results.Unauthorized() for its 401.
    private static readonly IResult Forbidden = Results.StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>
    /// The wire code for an authority that could not be established. Stable, because clients branch on
    /// it to tell this apart from a gateway's own 502 when the whole service is down.
    /// </summary>
    public const string UnavailableCode = "authority_unavailable";

    // 502, matching the status kgsm-api reports a DiscordAuthException with, so one upstream failure has
    // one meaning across the ecosystem. The JSON body is what separates it from a reverse proxy's 502
    // for a dead leaf, which carries no envelope.
    private static readonly IResult Unavailable = Results.Json(
        new
        {
            error = UnavailableCode,
            message = "Discord could not be reached to check your access. This is an outage upstream, "
                + "not a change to your permissions — try again in a moment.",
        },
        statusCode: StatusCodes.Status502BadGateway);

    private readonly AuthService _auth;

    public AdminOnlyFilter(AuthService auth) => _auth = auth;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Relay path: the tier was resolved upstream by a caller we trust, so it is the whole answer —
        // a relayed request never falls through to a Discord lookup for an identity it forwarded, since
        // a relay host may have no Discord configuration of its own. It is always a verdict, never an
        // outage: the api already resolved it, and an api that could not would not have forwarded.
        if (http.Items.TryGetValue(BearerAuthFilter.RelayTierKey, out var relay))
            return relay is KgsmTier tier && tier >= KgsmTier.Admin ? await next(context) : Forbidden;

        var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
        TierResolution authority = await _auth.ResolveReviewAuthorityAsync(principal, http.RequestAborted);

        if (!authority.Known)
            return Unavailable;

        return authority.Tier >= KgsmTier.Admin ? await next(context) : Forbidden;
    }
}
