using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Discord;

/// <summary>Discord's OAuth token-exchange response (snake_case on the wire).</summary>
internal sealed record DiscordTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = string.Empty;
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string Scope { get; init; } = string.Empty;
}

/// <summary>The caller's member object within a guild: identity + role snowflakes.</summary>
internal sealed record DiscordGuildMember
{
    [JsonPropertyName("roles")] public string[] Roles { get; init; } = Array.Empty<string>();
    [JsonPropertyName("user")] public DiscordUser? User { get; init; }
}

/// <summary>A Discord user. <c>global_name</c> is the new display name and may be null (use username then).</summary>
internal sealed record DiscordUser
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("username")] public string Username { get; init; } = string.Empty;
    [JsonPropertyName("global_name")] public string? GlobalName { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName!;
}

internal interface IDiscordOAuthClient
{
    /// <summary>Exchanges an authorization code (+ PKCE verifier) for an access token. Null on any failure.</summary>
    Task<DiscordTokenResponse?> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default);

    /// <summary>
    /// Fetches the authenticated user's identity (<c>/users/@me</c>) with their access token.
    /// This is the ONLY use of the caller's token — it is discarded immediately after. Null on failure.
    /// </summary>
    Task<DiscordUser?> GetCurrentUserAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Fetches a user's member object (roles) in the configured guild using the BOT token —
    /// no caller token involved. Returns null if they are NOT in the guild (404) or on a
    /// transient denial (429/other); a 401 means the bot token itself is misconfigured.
    /// </summary>
    Task<DiscordGuildMember?> GetGuildMemberAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Typed <see cref="HttpClient"/> over the Discord API. Registered with
/// <c>AddHttpClient&lt;IDiscordOAuthClient, DiscordOAuthClient&gt;</c> so the factory owns the
/// handler lifetime (this type is transient — never captured by a singleton). The bearer is
/// set per-request on the message, never on the shared client's default headers.
/// </summary>
internal sealed class DiscordOAuthClient : IDiscordOAuthClient
{
    private readonly HttpClient _http;
    private readonly DiscordOAuthOptions _options;
    private readonly ILogger<DiscordOAuthClient> _logger;

    public DiscordOAuthClient(HttpClient http, IOptions<DiscordOAuthOptions> options, ILogger<DiscordOAuthClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DiscordTokenResponse?> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code_verifier"] = codeVerifier,
        });

        using var response = await _http.PostAsync("api/v10/oauth2/token", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Discord token exchange failed: {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DiscordTokenResponse>(ct);
    }

    public async Task<DiscordUser?> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v10/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Discord /users/@me failed: {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DiscordUser>(ct);
    }

    public async Task<DiscordGuildMember?> GetGuildMemberAsync(string userId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v10/guilds/{_options.GuildId}/members/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);

        using var response = await _http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return await response.Content.ReadFromJsonAsync<DiscordGuildMember>(ct);
            case HttpStatusCode.NotFound:
                return null; // not a member of the guild → deny
            case HttpStatusCode.Unauthorized:
                // The BOT token is invalid — a server misconfiguration, not the caller's problem.
                _logger.LogError("Discord member lookup got 401 — DiscordOAuth:BotToken is missing or invalid.");
                return null;
            case HttpStatusCode.TooManyRequests:
                _logger.LogWarning("Discord member lookup rate-limited (429) — denying this check");
                return null; // the role cache throttles; surfacing a transient deny beats a retry loop
            default:
                _logger.LogWarning("Discord member lookup failed: {Status}", (int)response.StatusCode);
                return null;
        }
    }
}
