using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Discord;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>A resolved, logged-in caller. <see cref="SessionToken"/> is the bearer — server-side only.</summary>
internal sealed record AuthPrincipal(string UserId, string DisplayName, string SessionToken);

/// <summary>The outcome of a successful login: the session bearer to hand the SPA + the display name.</summary>
internal sealed record AuthSessionResult(string SessionToken, string DisplayName);

/// <summary>
/// Orchestrates the Discord OAuth login and computes authority. Scoped: it depends on the
/// transient typed <see cref="IDiscordOAuthClient"/>, so it must never be captured by a
/// singleton (the stores it uses ARE singletons and are injected, not owned).
/// <para>
/// Authority is the ecosystem's ordered tier, resolved from the shared role map, so a person gets
/// the same authority here as through the Control Panel and the Discord bot. It is re-derived fresh
/// (cached briefly) rather than stored on the session, so a revoked role takes effect within the
/// cache TTL.
/// </para>
/// </summary>
internal sealed class DiscordAuthService
{
    private readonly IDiscordOAuthClient _oauth;
    private readonly SessionStore _sessions;
    private readonly OAuthStateStore _states;
    private readonly RoleCache _roleCache;
    private readonly ConfirmationTokenService _tokens;
    private readonly DiscordOAuthOptions _discord;
    private readonly KgsmAuthOptions _sharedAuth;
    private readonly KgsmRoleMap _roleMap;
    private readonly AuthOptions _auth;
    private readonly AssistantServiceOptions _assistant;
    private readonly ILogger<DiscordAuthService> _logger;

    public DiscordAuthService(
        IDiscordOAuthClient oauth,
        SessionStore sessions,
        OAuthStateStore states,
        RoleCache roleCache,
        ConfirmationTokenService tokens,
        IOptions<DiscordOAuthOptions> discord,
        IOptions<KgsmAuthOptions> sharedAuth,
        KgsmRoleMap roleMap,
        IOptions<AuthOptions> auth,
        IOptions<AssistantServiceOptions> assistant,
        ILogger<DiscordAuthService> logger)
    {
        _oauth = oauth;
        _sessions = sessions;
        _states = states;
        _roleCache = roleCache;
        _tokens = tokens;
        _discord = discord.Value;
        _sharedAuth = sharedAuth.Value;
        _roleMap = roleMap;
        _auth = auth.Value;
        _assistant = assistant.Value;
        _logger = logger;
    }

    /// <summary>Builds the Discord authorize URL (with a fresh single-use state + PKCE challenge).</summary>
    public string BuildLoginUrl()
    {
        var verifier = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = _states.Create(verifier);

        var query = string.Join('&', new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(_sharedAuth.ClientId)}",
            $"scope={Uri.EscapeDataString(_discord.Scopes)}",
            $"redirect_uri={Uri.EscapeDataString(_discord.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
        });
        return $"https://discord.com/api/oauth2/authorize?{query}";
    }

    /// <summary>
    /// Completes login: validates the single-use state, exchanges the code, verifies identity
    /// (<c>/users/@me</c>, the caller's token then discarded), requires guild membership, and
    /// mints a session. Returns the session bearer token, or null on any failure (bad state,
    /// exchange failure, or not a guild member).
    /// </summary>
    public async Task<AuthSessionResult?> CompleteLoginAsync(string code, string state, CancellationToken ct = default)
    {
        if (!_states.TryConsume(state, out var verifier))
        {
            _logger.LogWarning("OAuth callback rejected: unknown/expired/replayed state");
            return null;
        }

        var token = await _oauth.ExchangeCodeAsync(code, verifier, ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            return null;

        // Verify identity once with the caller's token, then discard it — nothing downstream
        // ever needs it again (roles come from the bot, by user id).
        var user = await _oauth.GetCurrentUserAsync(token.AccessToken, ct);
        if (user is null || string.IsNullOrEmpty(user.Id))
            return null;

        // Authority is the bot's to resolve: fetch the caller's member object by user id. A
        // null result (404) means they are not in the configured guild → access denied.
        var member = await _oauth.GetGuildMemberAsync(user.Id, ct);
        if (member is null)
            return null;

        var session = new Session(
            user.Id,
            user.DisplayName,
            DateTimeOffset.UtcNow.AddSeconds(_auth.SessionTtlSeconds > 0 ? _auth.SessionTtlSeconds : 3600));

        var sessionToken = _sessions.Create(session);

        // Seed the tier cache from the member we already have — saves the first re-fetch. One
        // resolution answers every authority question, so nothing else needs looking up here.
        _roleCache.Set(user.Id, _roleMap.Resolve(member.Roles));

        return new AuthSessionResult(sessionToken, user.DisplayName);
    }

    /// <summary>Resolves a bearer token to a principal, or false if the session is unknown/expired.</summary>
    public bool TryResolvePrincipal(string? sessionToken, out AuthPrincipal principal)
    {
        principal = null!;
        if (!_sessions.TryGet(sessionToken, out var session))
            return false;

        principal = new AuthPrincipal(session.DiscordUserId, session.DisplayName, sessionToken!);
        return true;
    }

    /// <summary>
    /// Whether this principal may perform mutating/destructive actions RIGHT NOW. The master
    /// kill-switch (ActionsEnabled + a signing key + a configured operator role) is checked live;
    /// the per-user tier is served from a short-TTL cache, else re-fetched from Discord with the BOT
    /// token (by user id). No caller token is involved, so a re-check never forces a re-login.
    /// </summary>
    public async Task<bool> CanPerformActionsAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!_assistant.ActionsEnabled || !_tokens.IsConfigured || _roleMap.IsEmpty)
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
        if (_roleMap.AdminRoleIds.Count == 0)
            return false;

        return await ResolveTierAsync(principal, ct) >= KgsmTier.Admin;
    }

    /// <summary>
    /// The tier this principal holds, cached for the role-cache TTL. A null member — they left the
    /// guild, or Discord denied the lookup transiently — resolves to <see cref="KgsmTier.None"/> and
    /// is cached like any other answer, so a denial does not retry on every call. The session itself
    /// stays valid until its own expiry; there is no caller token to expire and force a re-login.
    /// </summary>
    private async Task<KgsmTier> ResolveTierAsync(AuthPrincipal principal, CancellationToken ct)
    {
        if (_roleCache.TryGet(principal.UserId, out KgsmTier cached))
            return cached;

        DiscordGuildMember? member = await _oauth.GetGuildMemberAsync(principal.UserId, ct);
        KgsmTier tier = _roleMap.Resolve(member?.Roles);
        _roleCache.Set(principal.UserId, tier);
        return tier;
    }

    public void Logout(AuthPrincipal principal)
    {
        _sessions.Remove(principal.SessionToken);
        _roleCache.Remove(principal.UserId);
    }


}
