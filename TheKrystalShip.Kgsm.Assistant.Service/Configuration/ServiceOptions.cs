// NOTE: KgsmConnectionOptions, InventoryCacheOptions, and WebSearchOptions moved to
// TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration when the kgsm-lib adapters were
// extracted into the shared infra library (both this service and the CLI bind them via
// AddKgsmAdapters). The web-only options below stay here.

namespace TheKrystalShip.Kgsm.Assistant.Service.Configuration;

/// <summary>Assistant-service policy and secrets. Bound from the "Assistant" section.</summary>
public sealed class AssistantServiceOptions
{
    public const string Section = "Assistant";

    /// <summary>
    /// Step-2 stand-in for authorization: whether this service may perform mutating /
    /// destructive actions at all. Authority is ALWAYS derived server-side — never from
    /// the request body. Step 3 replaces this flag with a verified Discord-OAuth principal.
    /// </summary>
    public bool ActionsEnabled { get; set; }

    public ConfirmationOptions Confirmation { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
    public RelayOptions Relay { get; set; } = new();
}

/// <summary>
/// Shared secret for a trusted, co-located <em>relay</em> (the per-host kgsm-api Control Panel
/// API) that calls the assistant on a verified end-user's behalf. Mirrors <see cref="WebhookOptions"/>:
/// when set, a request bearing a matching <c>X-Relay-Secret</c> is authenticated as the forwarded
/// Discord identity (<c>X-Relay-User</c>/<c>X-Relay-User-Name</c>) WITHOUT a session login; when
/// empty the relay path is disabled and only session bearers are accepted. The relay forwards
/// IDENTITY only — authority (can-perform) is still derived server-side from the bot by user id,
/// so the secret-holder cannot escalate a user beyond their real Discord role. The secret lives in
/// the same co-located trust domain as the API and the bot.
/// </summary>
public sealed class RelayOptions
{
    public string Secret { get; set; } = string.Empty;
}

/// <summary>HMAC key + lifetime for stateless confirmation tokens.</summary>
public sealed class ConfirmationOptions
{
    /// <summary>Secret used to sign confirmation tokens. Must be set for actions to be confirmable.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>How long a staged confirmation token stays valid. Keep short — re-validation backstops replay.</summary>
    public int TtlSeconds { get; set; } = 300;
}

/// <summary>Shared secret for verifying inbound kgsm webhook signatures.</summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Must match kgsm's <c>webhook_secret</c>. When set, <c>/events</c> requires a valid
    /// <c>X-KGSM-Signature</c>. When empty, signatures are not enforced (dev only).
    /// </summary>
    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// Discord OAuth2 settings for the web auth flow. Bound from the "DiscordOAuth" section.
/// The service runs the authorization-code exchange server-side (the SPA is a public
/// static host and can't hold a secret) and verifies guild membership + the action role
/// against the SAME role the Discord bot enforces.
/// <para>
/// Snowflakes (<see cref="GuildId"/>, <see cref="ActionRoleId"/>) are kept as STRINGS:
/// Discord's member <c>roles</c> array is a list of string snowflakes, so we compare as
/// strings and never risk a parse.
/// </para>
/// </summary>
public sealed class DiscordOAuthOptions
{
    public const string Section = "DiscordOAuth";

    /// <summary>The OAuth client id — this is the bot's existing Discord application id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client secret. ENV-ONLY: leave empty in appsettings.json and supply
    /// <c>DiscordOAuth__ClientSecret</c> via the environment at runtime.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Bot token for the SAME Discord application. Roles are resolved with this
    /// (<c>GET /guilds/{guild}/members/{user}</c>) — NOT the caller's OAuth token — so the
    /// login scope stays <c>identify</c>-only and no Discord token is ever retained. Required
    /// for any login to succeed (the guild-membership check uses it too).
    /// ENV-ONLY: supply <c>DiscordOAuth__BotToken</c> via the environment at runtime.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>The guild (Discord server) whose membership + role gate access.</summary>
    public string GuildId { get; set; } = string.Empty;

    /// <summary>
    /// Role whose holders may perform mutating/destructive actions — the SAME role the
    /// bot's <c>DiscordOptions.ActionRoleId</c> checks. Empty/"0" → no one is authorized.
    /// </summary>
    public string ActionRoleId { get; set; } = string.Empty;

    /// <summary>Where Discord redirects after authorize — the SPA's callback URL (HTTPS).</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scopes. <c>identify</c> only: we need just the verified user id (via
    /// <c>/users/@me</c>); roles are read with the bot token, never the caller's.
    /// </summary>
    public string Scopes { get; set; } = "identify";
}

/// <summary>
/// Web session / authority-caching policy. Bound from the "Auth" section. Authority is
/// never baked into a session — it is a role lookup cached for <see cref="RoleCacheTtlSeconds"/>
/// and re-read at confirm time.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>How long a minted session bearer stays valid. Keep ≤ the Discord token lifetime.</summary>
    public int SessionTtlSeconds { get; set; } = 3600;

    /// <summary>How long a per-user action-role decision is cached, to throttle Discord calls.</summary>
    public int RoleCacheTtlSeconds { get; set; } = 60;

    /// <summary>How long an in-flight authorize→callback <c>state</c> stays valid (single-use anyway).</summary>
    public int StateTtlSeconds { get; set; } = 300;

    /// <summary>Exact SPA origins (scheme + host, no trailing slash) allowed by CORS.</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}
