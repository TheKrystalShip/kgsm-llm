namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter gating the review surface — the endpoints that read OTHER users' conversations.
/// Runs after <see cref="BearerAuthFilter"/> (which has already established WHO the caller is) and
/// answers only WHETHER they may review, from whichever path authenticated them:
/// <list type="bullet">
/// <item><b>Trusted relay:</b> the api's verified admin decision, forwarded as <c>X-Relay-Admin</c>
/// and stashed as <see cref="BearerAuthFilter.RelayAdminKey"/>. Trusted because the relay secret
/// already matched.</item>
/// <item><b>Session bearer:</b> the caller's own Discord review role
/// (<see cref="DiscordAuthService.IsAdminAsync"/>), so the leaf's review surface works standalone,
/// with no api in front of it. No configured review role ⇒ nobody.</item>
/// </list>
/// Fail-closed on both paths: an unauthenticated request never reaches here (401 already), and an
/// authenticated non-admin gets a clean 403 without the handler running.
/// </summary>
internal sealed class AdminOnlyFilter : IEndpointFilter
{
    // A plain 403, not Results.Forbid(): this service authenticates by hand and registers no
    // authentication scheme, so Forbid() — which asks a scheme to write the challenge — throws and
    // surfaces as a 500. Same reason BearerAuthFilter returns Results.Unauthorized() for its 401.
    private static readonly IResult Forbidden = Results.StatusCode(StatusCodes.Status403Forbidden);

    private readonly DiscordAuthService _auth;

    public AdminOnlyFilter(DiscordAuthService auth) => _auth = auth;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Relay path: the decision was made upstream by a caller we trust, so it is the whole answer —
        // a relayed request never falls through to a Discord lookup for an identity it forwarded.
        if (http.Items.TryGetValue(BearerAuthFilter.RelayAdminKey, out var relay))
            return relay is true ? await next(context) : Forbidden;

        var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
        return await _auth.IsAdminAsync(principal, http.RequestAborted)
            ? await next(context)
            : Forbidden;
    }
}
