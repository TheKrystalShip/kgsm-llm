using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// A resolved, authenticated caller.
/// </summary>
/// <param name="Provider">Which identity provider verified this caller.</param>
/// <param name="UserId">
/// The provider's own id for them, unqualified — the identity every memory key is scoped by. It stays
/// unqualified because it keys conversation history that is already written; the provider travels
/// beside it rather than being folded in.
/// </param>
/// <param name="DisplayName">For display only. Never authority.</param>
/// <param name="SessionId">
/// The session this caller's token belongs to, or empty on the trusted-relay path, which has no
/// session of its own (the relay authenticated the user upstream). It is what a logout revokes and
/// what the registry can kill.
/// </param>
/// <param name="TokenTier">
/// The tier recorded when this session was created. A snapshot for display, <b>never</b> the authority
/// check — <see cref="AuthService.ResolveTierAsync"/> re-derives that, so a role
/// taken away stops working within the cache TTL instead of surviving until the token expires.
/// </param>
internal sealed record AuthPrincipal(
    string Provider,
    string UserId,
    string DisplayName,
    string SessionId,
    KgsmTier TokenTier = KgsmTier.None)
{
    /// <summary>
    /// This caller as an identity, for asking the authority what they may do. The profile fields are
    /// what the principal carries and no more — an authority resolves on who someone is, never on how
    /// prettily they are labelled.
    /// </summary>
    public KgsmIdentity AsIdentity() => new(Provider, UserId, UserId, DisplayName, null, []);

    /// <summary>The provider-qualified handle — what a per-user cache is keyed by.</summary>
    public string Handle => KgsmActor.Format(Provider, UserId);
}

/// <summary>
/// The answer to an authority question, with <em>"we could not ask"</em> kept apart from <em>"the
/// answer is no"</em>. <see cref="Tier"/> carries a verdict only when <see cref="Known"/>; an unknown
/// resolution means the authority could not be reached and this caller's authority is simply not established
/// right now.
/// </summary>
/// <remarks>
/// The distinction exists because the two facts want different reporting. A denial is terminal and
/// belongs to the caller — a <c>403</c>, and a surface that says "you don't have access". An unknown
/// is an upstream outage that belongs to the host — a <c>502</c>, and a surface that says "we couldn't
/// check", which is both true and worth retrying. Collapsing them tells an admin they lost a role they
/// still hold.
/// </remarks>
internal readonly record struct TierResolution(bool Known, KgsmTier Tier)
{
    /// <summary>The authority could not be asked; no verdict is carried.</summary>
    public static readonly TierResolution Unknown = new(false, KgsmTier.None);

    /// <summary>A verdict that was actually established — including a denial.</summary>
    public static TierResolution Of(KgsmTier tier) => new(true, tier);

    /// <summary>
    /// The tier for a decision that must produce an answer either way. An unknown resolution floors to
    /// <see cref="KgsmTier.None"/>, so a caller that cannot report an outage still denies during one.
    /// </summary>
    public KgsmTier OrNone => Known ? Tier : KgsmTier.None;
}

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
/// Runs the login and answers what a caller may do. Scoped: it depends on the transient sign-in seam,
/// so it must never be captured by a singleton (the caches and the registry it uses ARE singletons and
/// are injected, not owned).
/// </summary>
/// <remarks>
/// <para>
/// Authority is the ecosystem's ordered tier resolved through the shared seam, so a person gets the
/// same authority here as through the Control Panel and the Discord bot. It is re-derived fresh
/// (cached briefly) rather than read off the token, which is what makes a revoked role take effect
/// without waiting for a re-login.
/// </para>
/// <para>
/// This surface runs standalone: it holds its own identity-provider credentials and its own session
/// registry, and needs no kgsm-api in front of it to authenticate anyone.
/// </para>
/// </remarks>
internal sealed class AuthService(
    ISignInService signIn,
    IAuthorityProvider authority,
    ISessionTokenService tokens,
    ISessionRegistry sessions,
    ISessionValidator validator,
    KgsmTierCache tierCache,
    IOptions<AuthOptions> authOptions,
    IOptions<AssistantServiceOptions> assistantOptions,
    ILogger<AuthService> logger)
{
    private readonly AuthOptions _auth = authOptions.Value;
    private readonly AssistantServiceOptions _assistant = assistantOptions.Value;

    /// <summary>The provider's authorize URL for this handshake.</summary>
    public string BuildAuthorizeUrl(OAuthHandshake handshake, string? prompt) =>
        signIn.BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, prompt ?? "none");

    /// <summary>
    /// Exchange an authorization code for a verified identity and the tier this host grants it.
    /// <see langword="null"/> means the code itself was bad; a <see cref="KgsmAuthProviderException"/>
    /// means the provider could not be asked, which the caller reports as an upstream failure and never
    /// as a denial. A returned <see cref="KgsmTier.None"/> is the denial: identity verified, no access
    /// here.
    /// </summary>
    public Task<ResolvedPrincipal?> ResolveAsync(string code, string codeVerifier, CancellationToken ct) =>
        signIn.ResolveAsync(code, codeVerifier, ct);

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
                sessionId, resolved.Identity.Subject, _auth.ResolveHostId(),
                Created: now, Expires: refresh.ExpiresAt,
                UserAgent: userAgent, CurrentJti: refresh.Jti),
            ct);

        // Seed the tier cache from the resolution just made — the first request after login would
        // otherwise ask the authority the question it was already asked.
        tierCache.Set(resolved.Identity.Handle, resolved.Tier);

        logger.LogInformation(
            "{Provider} login: {Display} ({UserId}) → {Tier}, session {SessionId}",
            resolved.Identity.Provider, resolved.Identity.Display, resolved.Identity.Subject,
            KgsmTiers.ToWire(resolved.Tier), sessionId);

        return new AuthSessionResult(
            access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt,
            resolved.Tier, resolved.Identity.Subject, resolved.Identity.Display);
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
        // depend on the provider being reachable. It is a display value anyway — every authority check
        // re-derives.
        return new AuthSessionResult(
            access.Token, access.ExpiresAt, refresh.Token, refresh.ExpiresAt,
            claims.Tier, claims.Identity.Subject, claims.Identity.Display);
    }

    /// <summary>
    /// Whether this principal may perform mutating/destructive actions right now. The master
    /// kill-switch (actions enabled) is checked live; the per-user tier comes from the short-TTL
    /// cache, else from the authority. No caller token is involved, so a re-check never forces a
    /// re-login.
    /// </summary>
    /// <remarks>
    /// An unresolvable authority denies here rather than propagating: this answers whether to OFFER an
    /// action, and the worst it costs during an outage is that a mutation is staged for
    /// confirmation instead of running immediately. The review gate reports the outage instead, because
    /// there the alternative is a blank page the operator has to diagnose.
    /// </remarks>
    public async Task<bool> CanPerformActionsAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!_assistant.ActionsEnabled)
            return false;

        return (await ResolveTierAsync(principal, ct)).OrNone >= KgsmTier.Operator;
    }

    /// <summary>
    /// Whether this principal may read OTHER users' conversations (the review surface), as a three-way
    /// answer: granted, denied, or unknown because the authority could not be asked. Reading someone's chat is
    /// an administrator's power, the same tier that configures the host from the Control Panel — one
    /// ladder, so a person cannot hold a power on one surface that they lack on another.
    /// </summary>
    public Task<TierResolution> ResolveReviewAuthorityAsync(
        AuthPrincipal principal, CancellationToken ct = default) =>
        ResolveTierAsync(principal, ct);

    /// <summary>
    /// Whether this principal holds the review tier, for the callers that need a plain yes/no. An
    /// unresolvable authority reads as no; a caller that must tell an outage apart from a denial asks
    /// <see cref="ResolveReviewAuthorityAsync"/> instead.
    /// </summary>
    public async Task<bool> IsAdminAsync(AuthPrincipal principal, CancellationToken ct = default) =>
        (await ResolveReviewAuthorityAsync(principal, ct)).OrNone >= KgsmTier.Admin;

    /// <summary>
    /// The tier this principal holds, cached for the role-cache TTL. A denial resolves to
    /// <see cref="KgsmTier.None"/> and is cached like any other answer, so it does not reach the
    /// authority on every call.
    /// </summary>
    /// <remarks>
    /// A failure to reach the authority returns an UNKNOWN resolution and is <b>not</b> cached. "We
    /// could not ask" is a different fact from "the answer is no": storing the first as the second would
    /// turn a thirty-second outage into a full-TTL lockout for someone who is genuinely an operator,
    /// and reporting it as the second tells that person they lost a role they still hold. Each caller
    /// decides what an unknown costs it — see <see cref="CanPerformActionsAsync"/> and
    /// <see cref="ResolveReviewAuthorityAsync"/>. The session itself stays valid throughout — there is
    /// no caller token to expire.
    /// </remarks>
    public async Task<TierResolution> ResolveTierAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (tierCache.TryGet(principal.Handle, out KgsmTier cached))
            return TierResolution.Of(cached);

        KgsmTier tier;
        try
        {
            tier = await authority.ResolveTierAsync(principal.AsIdentity(), ct);
        }
        catch (KgsmAuthProviderException ex)
        {
            logger.LogWarning(
                ex, "Could not resolve authority for {UserId} — reporting it as unavailable, not as a denial.",
                principal.UserId);
            return TierResolution.Unknown;
        }

        tierCache.Set(principal.Handle, tier);
        return TierResolution.Of(tier);
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

        tierCache.Remove(principal.Handle);
    }
}
