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
    /// Completes login: validates the single-use state, exchanges the code, requires guild
    /// membership, and mints a session. Returns the session bearer token, or null on any
    /// failure (bad state, exchange failure, or not a guild member).
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

        DiscordGuildMember? member;
        try
        {
            member = await _oauth.GetGuildMemberAsync(token.AccessToken, ct);
        }
        catch (DiscordTokenExpiredException)
        {
            return null; // freshly-issued token rejected (e.g. missing guilds.members.read scope)
        }

        if (member?.User is null || string.IsNullOrEmpty(member.User.Id))
            return null; // not a member of the configured guild → access denied

        var session = new Session(
            member.User.Id,
            member.User.DisplayName,
            token.AccessToken,
            DateTimeOffset.UtcNow.AddSeconds(_auth.SessionTtlSeconds > 0 ? _auth.SessionTtlSeconds : 3600));

        var sessionToken = _sessions.Create(session);

        // Seed the role cache from the member we already have — saves the first re-fetch.
        _roleCache.Set(member.User.Id, HasActionRole(member.Roles));

        return new AuthSessionResult(sessionToken, member.User.DisplayName);
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
    /// using the session's retained token. A 401 evicts the session (forces re-login).
    /// </summary>
    public async Task<bool> CanPerformActionsAsync(AuthPrincipal principal, CancellationToken ct = default)
    {
        if (!_assistant.ActionsEnabled || !_tokens.IsConfigured || !ActionRoleConfigured)
            return false;

        if (_roleCache.TryGet(principal.UserId, out var cached))
            return cached;

        if (!_sessions.TryGet(principal.SessionToken, out var session))
            return false;

        DiscordGuildMember? member;
        try
        {
            member = await _oauth.GetGuildMemberAsync(session.AccessToken, ct);
        }
        catch (DiscordTokenExpiredException)
        {
            _sessions.Remove(principal.SessionToken);
            _roleCache.Remove(principal.UserId);
            return false;
        }

        var hasRole = member is not null && HasActionRole(member.Roles);
        _roleCache.Set(principal.UserId, hasRole);
        return hasRole;
    }

    public void Logout(AuthPrincipal principal)
    {
        _sessions.Remove(principal.SessionToken);
        _roleCache.Remove(principal.UserId);
    }

    private bool ActionRoleConfigured =>
        !string.IsNullOrEmpty(_discord.ActionRoleId) && _discord.ActionRoleId != "0";

    private bool HasActionRole(string[] roles) =>
        ActionRoleConfigured && roles.Contains(_discord.ActionRoleId, StringComparer.Ordinal);
}
