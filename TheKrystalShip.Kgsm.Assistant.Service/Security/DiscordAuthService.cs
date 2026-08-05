using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Discord;

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
/// Authority mirrors the bot exactly — a caller may act iff they hold the configured
/// <c>ActionRoleId</c> in the guild — and is re-derived fresh (cached briefly) rather than
/// stored on the session, so a revoked role takes effect within the cache TTL.
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
            $"client_id={Uri.EscapeDataString(_discord.ClientId)}",
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

        // Seed the role cache from the member we already have — saves the first re-fetch. Both roles
        // come off the same member object, so the review decision costs nothing extra here.
        _roleCache.Set(_discord.ActionRoleId, user.Id, HasActionRole(member.Roles));
        if (AdminRoleConfigured)
            _roleCache.Set(_discord.AdminRoleId, user.Id, member.Roles.Contains(_discord.AdminRoleId, StringComparer.Ordinal));

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
    /// kill-switch (ActionsEnabled + a signing key + a configured action role) is checked live;
    /// the per-user role decision is served from a short-TTL cache, else re-fetched from Discord
    /// with the BOT token (by user id). No caller token is involved, so a role re-check never
    /// forces a re-login.
    /// </summary>
    public async Task<bool> CanPerformActionsAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!_assistant.ActionsEnabled || !_tokens.IsConfigured || !ActionRoleConfigured)
            return false;

        if (_roleCache.TryGet(_discord.ActionRoleId, principal.UserId, out var cached))
            return cached;

        // Re-derive authority from the bot by user id. A null member (left the guild, or a
        // transient denial) simply denies for this cache TTL — the session itself stays valid
        // until its own expiry; there is no caller token to expire and force a re-login.
        var member = await _oauth.GetGuildMemberAsync(principal.UserId, ct);
        var hasRole = member is not null && HasActionRole(member.Roles);
        _roleCache.Set(_discord.ActionRoleId, principal.UserId, hasRole);
        return hasRole;
    }

    /// <summary>
    /// Whether this principal may read OTHER users' conversations (the review surface). Same live
    /// derivation as <see cref="CanPerformActionsAsync"/> — the bot, by user id, behind the same
    /// short-TTL cache — against its OWN role: acting on a server and reading someone's chat are
    /// different powers. No configured review role ⇒ nobody, so a host that never set one cannot have
    /// the surface opened by a session bearer.
    /// </summary>
    public async Task<bool> IsAdminAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!AdminRoleConfigured)
            return false;

        if (_roleCache.TryGet(_discord.AdminRoleId, principal.UserId, out var cached))
            return cached;

        var member = await _oauth.GetGuildMemberAsync(principal.UserId, ct);
        var hasRole = member is not null && member.Roles.Contains(_discord.AdminRoleId, StringComparer.Ordinal);
        _roleCache.Set(_discord.AdminRoleId, principal.UserId, hasRole);
        return hasRole;
    }

    public void Logout(AuthPrincipal principal)
    {
        _sessions.Remove(principal.SessionToken);
        _roleCache.Remove(principal.UserId);
    }

    private bool ActionRoleConfigured =>
        !string.IsNullOrEmpty(_discord.ActionRoleId) && _discord.ActionRoleId != "0";

    private bool AdminRoleConfigured =>
        !string.IsNullOrEmpty(_discord.AdminRoleId) && _discord.AdminRoleId != "0";

    private bool HasActionRole(string[] roles) =>
        ActionRoleConfigured && roles.Contains(_discord.ActionRoleId, StringComparer.Ordinal);
}
