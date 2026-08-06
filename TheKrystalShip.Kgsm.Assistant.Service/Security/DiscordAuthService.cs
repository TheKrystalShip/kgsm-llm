using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// A resolved, authenticated caller.
/// </summary>
/// <param name="UserId">The verified Discord user id — the identity every memory key is scoped by.</param>
/// <param name="DisplayName">For display only. Never authority.</param>
/// <param name="SessionId">
/// The session this caller's token belongs to, or empty on the trusted-relay path, which has no
/// session of its own (the relay authenticated the user upstream). It is what a logout revokes and
/// what the registry can kill.
/// </param>
/// <param name="TokenTier">
/// The tier recorded when this session was created. A snapshot for display, <b>never</b> the authority
/// check — <see cref="DiscordAuthService.ResolveTierAsync"/> re-derives that from Discord, so a role
/// taken away stops working within the cache TTL instead of surviving until the token expires.
/// </param>
internal sealed record AuthPrincipal(
    string UserId,
    string DisplayName,
    string SessionId,
    KgsmTier TokenTier = KgsmTier.None);

/// <summary>The tokens a completed login (or a refresh) hands back.</summary>
internal sealed record AuthSessionResult(
    string AccessToken,
    DateTimeOffset AccessExpires,
    string RefreshToken,
    DateTimeOffset RefreshExpires,
    KgsmTier Tier,
    string UserId,
    string DisplayName);

/// <summary>
/// Runs the Discord login and answers what a caller may do. Scoped: it depends on the transient typed
/// <see cref="IDiscordDirectory"/>, so it must never be captured by a singleton (the caches and the
/// registry it uses ARE singletons and are injected, not owned).
/// </summary>
/// <remarks>
/// <para>
/// Authority is the ecosystem's ordered tier resolved from the shared role map, so a person gets the
/// same authority here as through the Control Panel and the Discord bot. It is re-derived fresh
/// (cached briefly) rather than read off the token, which is what makes a revoked role take effect
/// without waiting for a re-login.
/// </para>
/// <para>
/// This surface runs standalone: it holds its own Discord application credentials and its own session
/// registry, and needs no kgsm-api in front of it to authenticate anyone.
/// </para>
/// </remarks>
internal sealed class DiscordAuthService(
    IDiscordDirectory directory,
    ISessionTokenService tokens,
    ISessionRegistry sessions,
    ISessionValidator validator,
    DiscordTierCache tierCache,
    ConfirmationTokenService confirmations,
    KgsmRoleMap roleMap,
    IOptions<AuthOptions> authOptions,
    IOptions<AssistantServiceOptions> assistantOptions,
    ILogger<DiscordAuthService> logger)
{
    private readonly AuthOptions _auth = authOptions.Value;
    private readonly AssistantServiceOptions _assistant = assistantOptions.Value;

    /// <summary>The Discord authorize URL for this handshake.</summary>
    public string BuildAuthorizeUrl(OAuthHandshake handshake, string? prompt) =>
        directory.BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, prompt ?? "none");

    /// <summary>
    /// Exchange an authorization code for a verified identity and the tier this host grants it.
    /// <see langword="null"/> means the code itself was bad; a <see cref="DiscordAuthException"/> means
    /// Discord could not be asked, which the caller reports as an upstream failure and never as a
    /// denial. A returned <see cref="KgsmTier.None"/> is the denial: identity verified, no access here.
    /// </summary>
    public Task<ResolvedPrincipal?> ResolveAsync(string code, string codeVerifier, CancellationToken ct) =>
        directory.ResolveAsync(code, codeVerifier, ct);

    /// <summary>
    /// Record a login and mint its tokens. The refresh token's <c>jti</c> is stored as the session's
    /// current one — the value a later refresh must present, which is how a rotated-away token is told
    /// apart from the live one.
    /// </summary>
    public async Task<AuthSessionResult> CreateSessionAsync(
        ResolvedPrincipal resolved, string? userAgent, CancellationToken ct)
    {
        string sessionId = "sid_" + Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MintedToken access = tokens.MintAccess(resolved.Identity, resolved.Tier, sessionId);
        MintedToken refresh = tokens.MintRefresh(resolved.Identity, resolved.Tier, sessionId);

        await sessions.CreateAsync(
            new SessionRegistration(
                // The RESOLVED host, the same value the tokens are minted under. Reading the raw
                // setting here would write a row claiming the session belongs to host "" while its
                // tokens say otherwise — one host identity with two spellings, which is how a later
                // reconciliation quietly matches nothing.
                sessionId, resolved.Identity.UserId, _auth.ResolveHostId(),
                Created: now, Expires: refresh.ExpiresAt,
                UserAgent: userAgent, CurrentJti: refresh.Jti),
            ct);

        // Seed the tier cache from the resolution just made — the first request after login would
        // otherwise ask Discord the question it was already asked.
        tierCache.Set(resolved.Identity.UserId, resolved.Tier);

        logger.LogInformation(
            "Discord login: {Display} ({UserId}) → {Tier}, session {SessionId}",
            resolved.Identity.Display, resolved.Identity.UserId, KgsmTiers.ToWire(resolved.Tier), sessionId);

        return new AuthSessionResult(
            access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt,
            resolved.Tier, resolved.Identity.UserId, resolved.Identity.Display);
    }

    /// <summary>
    /// Trade a refresh token for a fresh pair, sliding the session's cap forward.
    /// <see langword="null"/> when the token is invalid, expired, not a refresh token, or presents a
    /// <c>jti</c> the session no longer holds.
    /// </summary>
    /// <remarks>
    /// A mismatched <c>jti</c> means the presented token has already been rotated away — either a stale
    /// client or a stolen token, and there is no way to tell which from here. The refusal is the same
    /// either way: no new tokens, and the legitimate holder signs in again.
    /// </remarks>
    public async Task<AuthSessionResult?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        RefreshClaims? claims = await tokens.ReadRefreshAsync(refreshToken);
        if (claims is null)
            return null;

        MintedToken access = tokens.MintAccess(claims.Identity, claims.Tier, claims.SessionId);
        MintedToken refresh = tokens.MintRefresh(claims.Identity, claims.Tier, claims.SessionId);

        if (!await sessions.RotateAsync(claims.SessionId, claims.Jti, refresh.Jti, refresh.ExpiresAt, ct))
        {
            logger.LogWarning(
                "Refresh refused for session {SessionId}: the presented token is not the current one "
                + "(rotated away, revoked, or past its cap)", claims.SessionId);
            return null;
        }

        // The tier travels forward from the token rather than being re-resolved: a refresh must not
        // depend on Discord being reachable. It is a display value anyway — every authority check
        // re-derives.
        return new AuthSessionResult(
            access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt,
            claims.Tier, claims.Identity.UserId, claims.Identity.Display);
    }

    /// <summary>
    /// Whether this principal may perform mutating/destructive actions right now. The master
    /// kill-switch (actions enabled + a confirmation signing key + a configured operator role) is
    /// checked live; the per-user tier comes from the short-TTL cache, else from Discord with the BOT
    /// token. No caller token is involved, so a re-check never forces a re-login.
    /// </summary>
    public async Task<bool> CanPerformActionsAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!_assistant.ActionsEnabled || !confirmations.IsConfigured || roleMap.IsEmpty)
            return false;

        return await ResolveTierAsync(principal, ct) >= KgsmTier.Operator;
    }

    /// <summary>
    /// Whether this principal may read OTHER users' conversations (the review surface). Reading
    /// someone's chat is an administrator's power, the same tier that configures the host from the
    /// Control Panel — one ladder, so a person cannot hold a power on one surface that they lack on
    /// another. No configured admin role ⇒ nobody, so a host that never set one cannot have the
    /// surface opened by a session bearer.
    /// </summary>
    public async Task<bool> IsAdminAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (roleMap.AdminRoleIds.Count == 0)
            return false;

        return await ResolveTierAsync(principal, ct) >= KgsmTier.Admin;
    }

    /// <summary>
    /// The tier this principal holds, cached for the role-cache TTL. Not being a member of the guild
    /// resolves to <see cref="KgsmTier.None"/> and is cached like any other answer, so a denial does not
    /// reach Discord on every call.
    /// </summary>
    /// <remarks>
    /// A failure to reach Discord denies this one check and is <b>not</b> cached. "We could not ask" is
    /// a different fact from "the answer is no", and storing the first as the second would turn a
    /// thirty-second Discord outage into a full-TTL lockout for someone who is genuinely an operator.
    /// The session itself stays valid throughout — there is no caller token to expire.
    /// </remarks>
    public async Task<KgsmTier> ResolveTierAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (tierCache.TryGet(principal.UserId, out KgsmTier cached))
            return cached;

        IReadOnlyList<string>? roles;
        try
        {
            roles = await directory.GetGuildRolesAsync(principal.UserId, ct);
        }
        catch (DiscordAuthException ex)
        {
            logger.LogWarning(ex, "Could not resolve authority for {UserId} — denying this check.", principal.UserId);
            return KgsmTier.None;
        }

        KgsmTier tier = roleMap.Resolve(roles);
        tierCache.Set(principal.UserId, tier);
        return tier;
    }

    /// <summary>
    /// End a session: revoke the row, drop the validator's cached answer so the kill is immediate
    /// rather than TTL-bounded, and forget the cached tier.
    /// </summary>
    public async Task LogoutAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (principal.SessionId.Length > 0)
        {
            await sessions.RevokeAsync(principal.SessionId, ct);
            validator.Evict(principal.SessionId);
        }

        tierCache.Remove(principal.UserId);
    }
}
