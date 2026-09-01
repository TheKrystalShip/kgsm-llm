using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter gating the review surface — the endpoints that read OTHER users' conversations.
/// Runs after <see cref="BearerAuthFilter"/> (which has already established WHO the caller is) and
/// answers only WHETHER they may review, from whichever path authenticated them:
/// <list type="bullet">
/// <item><b>Member acting:</b> the tier this member resolved for the person being acted for, stashed
/// as <see cref="BearerAuthFilter.ActingTierKey"/>, read from this member's own replica.</item>
/// <item><b>Session bearer:</b> the tier this service resolves for the caller from the account store,
/// through <see cref="AuthService.ResolveReviewAuthorityAsync"/>. Whichever provider vouched for them,
/// what they may do comes from the account record and nowhere else.</item>
/// </list>
/// Both paths read the same source, which is why neither takes an answer from the other: an acting
/// call names a person and this member resolves them, and a session names a person and this member
/// resolves them. A tier is never read off a bearer and never accepted from a caller.
/// <para>
/// Fail-closed on both paths: an unauthenticated request never reaches here (401 already), and an
/// authenticated non-admin gets a clean 403 without the handler running.
/// </para>
/// <para>
/// A third outcome is reported apart from those two: when the account store cannot be read, the gate
/// answers <c>502</c> with <c>authority_unavailable</c> rather than <c>403</c>. Access is refused
/// either way — nobody is admitted while authority is unknown — but a client told "denied" shows the
/// operator a permissions problem to go and investigate, when what happened is that a store was
/// briefly unreadable and the next request will succeed.
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

    // 502, the status kgsm-api reports an unreachable authority with, so one failure has one meaning
    // across the ecosystem. The JSON body is what separates it from a reverse proxy's 502 for a dead
    // leaf, which carries no envelope.
    private static readonly IResult Unavailable = Results.Json(
        new
        {
            error = UnavailableCode,
            message = "Your access could not be checked. This is an outage, not a change to your "
                + "permissions — try again in a moment.",
        },
        statusCode: StatusCodes.Status502BadGateway);

    private readonly AuthService _auth;

    public AdminOnlyFilter(AuthService auth) => _auth = auth;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Acting path: another member named the person it is acting for, and THIS service resolved that
        // person's tier against its own copy of the cluster's accounts before the request got here. So
        // the value stashed there is this member's own answer, not the caller's claim, and it is always
        // a verdict rather than an outage — a request whose authority could not be resolved is refused
        // before reaching this filter.
        if (http.Items.TryGetValue(BearerAuthFilter.ActingTierKey, out var acting))
            return acting is KgsmTier tier && tier >= KgsmTier.Admin ? await next(context) : Forbidden;

        var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
        TierResolution authority = await _auth.ResolveReviewAuthorityAsync(principal, http.RequestAborted);

        if (!authority.Known)
            return Unavailable;

        return authority.Tier >= KgsmTier.Admin ? await next(context) : Forbidden;
    }
}
