using TheKrystalShip.KGSM.LeafConfig;

// NOTE: KgsmConnectionOptions, InventoryCacheOptions, and WebSearchOptions moved to
// TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration when the kgsm-lib adapters were
// extracted into the shared infra library (both this service and the CLI bind them via
// AddKgsmAdapters). The web-only options below stay here.

namespace TheKrystalShip.Kgsm.Assistant.Service.Configuration;

/// <summary>Assistant-service policy and secrets. Bound from the "Assistant" section.</summary>
[LeafSection(Section)]
public sealed class AssistantServiceOptions
{
    public const string Section = "Assistant";

    /// <summary>
    /// Step-2 stand-in for authorization: whether this service may perform mutating /
    /// destructive actions at all. Authority is ALWAYS derived server-side — never from
    /// the request body. Step 3 replaces this flag with a verified Discord-OAuth principal.
    /// </summary>
    /// <panel>Whether the assistant may do anything, rather than only answer questions. With this off it
    /// can still read and explain, but every start, stop, install or edit is refused.</panel>
    [LeafField("actionsEnabled", "Allow actions", Group = "actions")]
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
    /// <panel>Shared secret letting the co-located Control Panel API ask on a signed-in user's behalf
    /// without a second login. It has to match the API's own relay secret, or the panel's chat
    /// stops working. The relay forwards who is asking, never what they are allowed to do.</panel>
    [LeafField("relaySecret", "Control Panel relay secret", Group = "actions", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__AssistantRelaySecret", NoDefault = true)]
    public string Secret { get; set; } = string.Empty;
}

/// <summary>HMAC key + lifetime for stateless confirmation tokens.</summary>
public sealed class ConfirmationOptions
{
    /// <summary>Secret used to sign confirmation tokens. Must be set for actions to be confirmable.</summary>
    /// <panel>Secret used to sign the token that carries a staged action from proposal to confirmation.
    /// Unset, nothing can be confirmed and every action is refused.</panel>
    [LeafField("confirmationKey", "Confirmation signing key", Group = "actions", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, DependsOn = "actionsEnabled", NoDefault = true)]
    public string Key { get; set; } = string.Empty;

    /// <summary>How long a staged confirmation token stays valid. Keep short — re-validation backstops replay.</summary>
    /// <panel>How long a proposed action stays confirmable. Keep it short: it is how long a stale
    /// confirmation could still be used.</panel>
    [LeafField("confirmationTtlSec", "Confirmation lifetime", Group = "actions", Min = 1, Unit = "s")]
    public int TtlSeconds { get; set; } = 300;
}

/// <summary>Shared secret for verifying inbound kgsm webhook signatures.</summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Must match kgsm's <c>webhook_secret</c>. When set, <c>/events</c> requires a valid
    /// <c>X-KGSM-Signature</c>. When empty, signatures are not enforced (dev only).
    /// </summary>
    /// <panel>Shared secret proving an inbound engine event really came from KGSM. It has to match the
    /// engine's own webhook secret. Unset, event signatures are not checked at all.</panel>
    [LeafField("webhookSecret", "Engine webhook secret", Group = "actions", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
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
[LeafSection(Section)]
public sealed class DiscordOAuthOptions
{
    public const string Section = "DiscordOAuth";

    /// <summary>The OAuth client id — this is the bot's existing Discord application id.</summary>
    /// <panel>The Discord application users sign in through. The same application as the bot's.</panel>
    [LeafField("discordClientId", "Discord application id", Group = "discord", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client secret. ENV-ONLY: leave empty in appsettings.json and supply
    /// <c>DiscordOAuth__ClientSecret</c> via the environment at runtime.
    /// </summary>
    /// <panel>Secret for that application, used to complete a sign-in server-side.</panel>
    [LeafField("discordClientSecret", "Discord client secret", Group = "discord", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Bot token for the SAME Discord application. Roles are resolved with this
    /// (<c>GET /guilds/{guild}/members/{user}</c>) — NOT the caller's OAuth token — so the
    /// login scope stays <c>identify</c>-only and no Discord token is ever retained. Required
    /// for any login to succeed (the guild-membership check uses it too).
    /// ENV-ONLY: supply <c>DiscordOAuth__BotToken</c> via the environment at runtime.
    /// </summary>
    /// <panel>Token for the same application, used to look up whether someone is in the server and holds
    /// the action role. Sign-in itself only ever asks Discord for a user's identity, never their
    /// roles.</panel>
    [LeafField("discordBotToken", "Discord bot token", Group = "discord", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string BotToken { get; set; } = string.Empty;

    /// <summary>The guild (Discord server) whose membership + role gate access.</summary>
    /// <panel>The Discord server whose membership decides who may sign in.</panel>
    [LeafField("discordGuildId", "Discord server id", Group = "discord", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string GuildId { get; set; } = string.Empty;

    /// <summary>
    /// Role whose holders may perform mutating/destructive actions — the SAME role the
    /// bot's <c>DiscordOptions.ActionRoleId</c> checks. Empty/"0" → no one is authorized.
    /// </summary>
    /// <panel>Role whose holders may perform actions — the same role the Discord bot checks. Empty means
    /// no one is authorized.</panel>
    [LeafField("discordActionRoleId", "Action role id", Group = "discord", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string ActionRoleId { get; set; } = string.Empty;

    /// <summary>
    /// Role whose holders may read OTHER users' conversations through the admin review surface.
    /// Empty/"0" → no session bearer can, and the surface is reachable only through a trusted relay
    /// that asserts admin itself. Deliberately its own role, not the action role: acting on a server
    /// and reading someone's chat are different powers.
    /// </summary>
    /// <panel>Role whose holders may read other people's assistant conversations, to review how the
    /// assistant is answering. Empty means nobody signing in here can — separate from the action role
    /// on purpose.</panel>
    [LeafField("discordAdminRoleId", "Review role id", Group = "discord", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string AdminRoleId { get; set; } = string.Empty;

    /// <summary>Where Discord redirects after authorize — the SPA's callback URL (HTTPS).</summary>
    /// <panel>Where Discord sends someone back to after they approve. It has to match the redirect
    /// registered on the Discord application exactly.</panel>
    [LeafField("discordRedirectUri", "Sign-in redirect", Group = "discord", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scopes. <c>identify</c> only: we need just the verified user id (via
    /// <c>/users/@me</c>); roles are read with the bot token, never the caller's.
    /// </summary>
    /// <panel>What sign-in asks Discord for. Identity alone is enough, because roles are read with the bot
    /// token instead.</panel>
    [LeafField("discordScopes", "Sign-in scopes", Group = "discord", Risk = LeafRisk.Wiring)]
    public string Scopes { get; set; } = "identify";
}

/// <summary>
/// Web session / authority-caching policy. Bound from the "Auth" section. Authority is
/// never baked into a session — it is a role lookup cached for <see cref="RoleCacheTtlSeconds"/>
/// and re-read at confirm time.
/// </summary>
[LeafSection(Section)]
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>How long a minted session bearer stays valid. Keep ≤ the Discord token lifetime.</summary>
    /// <panel>How long a sign-in lasts before the user has to sign in again.</panel>
    [LeafField("sessionTtlSec", "Session lifetime", Group = "session", Min = 60, Unit = "s")]
    public int SessionTtlSeconds { get; set; } = 3600;

    /// <summary>How long a per-user action-role decision is cached, to throttle Discord calls.</summary>
    /// <panel>How long a user's role decision is reused before Discord is asked again. Longer means fewer
    /// calls to Discord and a longer wait before a revoked role takes effect.</panel>
    [LeafField("roleCacheTtlSec", "Role cache lifetime", Group = "session", Min = 0, Unit = "s")]
    public int RoleCacheTtlSeconds { get; set; } = 60;

    /// <summary>How long an in-flight authorize→callback <c>state</c> stays valid (single-use anyway).</summary>
    /// <panel>How long someone has to finish a sign-in once it starts before they have to begin again.</panel>
    [LeafField("stateTtlSec", "Sign-in window", Group = "session", Min = 30, Unit = "s")]
    public int StateTtlSeconds { get; set; } = 300;

    /// <summary>Exact SPA origins (scheme + host, no trailing slash) allowed by CORS.</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}
