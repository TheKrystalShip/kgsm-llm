namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Endpoint filter that requires a valid session bearer token. Reads
/// <c>Authorization: Bearer &lt;token&gt;</c>, resolves it to an <see cref="AuthPrincipal"/>,
/// and stashes the principal on <see cref="HttpContext.Items"/> for the handler. A missing
/// or unresolvable token short-circuits with a clean 401 (never calling the handler).
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

    private const string BearerPrefix = "Bearer ";

    private readonly DiscordAuthService _auth;

    public BearerAuthFilter(DiscordAuthService auth) => _auth = auth;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();

        string? token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(token) || !_auth.TryResolvePrincipal(token, out var principal))
            return Results.Unauthorized();

        context.HttpContext.Items[PrincipalKey] = principal;
        return await next(context);
    }
}
