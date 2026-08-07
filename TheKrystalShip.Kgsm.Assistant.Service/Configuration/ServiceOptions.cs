using TheKrystalShip.KGSM.Auth.Sessions;
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
    /// <summary>How long a staged action stays confirmable. Keep short — re-validation backstops replay.</summary>
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
/// This service's own Discord OAuth2 endpoints. Bound from the "DiscordOAuth" section; the guild,
/// application credentials and role ids are the ecosystem's shared <c>KgsmAuth</c> block, so what is
/// left here is only what belongs to this surface.
/// <para>
/// The authorization-code exchange runs server-side, holding the client secret — a confidential
/// client — and the caller's token is discarded after one identity read. Roles come from the bot
/// token, so a user's grant never has to carry them.
/// </para>
/// </summary>
[LeafSection(Section)]
public sealed class DiscordOAuthOptions
{
    public const string Section = "DiscordOAuth";

    /// <summary>
    /// Where Discord returns the browser: this service's own <c>/auth/discord/callback</c>, reachable
    /// over HTTPS. It must match a redirect registered on the Discord application exactly.
    /// </summary>
    /// <panel>Where Discord sends someone back to after they approve — this service's own sign-in
    /// callback. It has to match the redirect registered on the Discord application exactly.</panel>
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
/// Web session policy. Bound from the "Auth" section.
/// </summary>
/// <remarks>
/// A sign-in yields a short-lived <em>access</em> bearer plus a <em>refresh</em> token that buys new
/// ones without going back to Discord, up to <see cref="SessionTtlSeconds"/>. Authority is never baked
/// into a session — it is a role lookup cached for <see cref="RoleCacheTtlSeconds"/> and re-read at
/// confirm time, so a revoked role takes effect within that TTL rather than at the next sign-in.
/// </remarks>
[LeafSection(Section)]
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// The HMAC secret session tokens are signed with. Unset generates an ephemeral per-process key,
    /// which is fine for a test run and means every restart signs everyone out on a real host.
    /// </summary>
    /// <panel>Secret this service signs sign-in tokens with. Generate one and keep it stable: change it,
    /// or leave it unset, and everyone is signed out the next time the service restarts.</panel>
    [LeafField("authSigningKey", "Session signing key", Group = "session", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// The token audience — a bearer minted here is scoped to this host and is refused by any other.
    /// Blank means the machine name, which is why the shipped default is empty rather than a literal:
    /// a settings file cannot name the host it will be installed on.
    /// </summary>
    /// <panel>The name this host's sign-in tokens are issued for. A token minted here will not be
    /// accepted anywhere else. Leave it empty to use the machine's own name.</panel>
    [LeafField("authHostId", "Host identity", Group = "session")]
    public string HostId { get; set; } = string.Empty;

    /// <summary>
    /// The audience tokens are actually minted under: <see cref="HostId"/>, or the machine name.
    /// </summary>
    /// <remarks>
    /// A method rather than a property so the descriptor generator does not read it as one more
    /// bindable key — it is derived from <see cref="HostId"/>, and a second way to set the same thing
    /// is how two halves of one setting drift apart.
    /// </remarks>
    public string ResolveHostId() =>
        string.IsNullOrWhiteSpace(HostId) ? Environment.MachineName : HostId;

    /// <summary>
    /// How long an access bearer lives. Short on purpose: it is what bounds privilege between
    /// re-checks, and the refresh token is what keeps the user signed in.
    /// </summary>
    /// <panel>How long a sign-in token is good for before the client silently swaps it for a fresh one.
    /// Shorter is safer; the user notices nothing either way.</panel>
    [LeafField("accessTtlSec", "Access token lifetime", Group = "session", Min = 60, Unit = "s")]
    public int AccessTtlSeconds { get; set; } = 900;

    /// <summary>
    /// The absolute cap on a sign-in: how long someone stays signed in, refreshing, before a fresh
    /// Discord login. Each successful refresh slides it forward.
    /// </summary>
    /// <panel>How long a sign-in lasts before the user has to sign in through Discord again. Every
    /// refresh slides the window forward, so someone who keeps using it stays signed in.</panel>
    [LeafField("sessionTtlSec", "Session lifetime", Group = "session", Min = 60, Unit = "s")]
    public int SessionTtlSeconds { get; set; } = 30 * 24 * 60 * 60;

    /// <summary>How long a per-user tier decision is cached, to throttle Discord calls.</summary>
    /// <panel>How long a user's role decision is reused before Discord is asked again. Longer means fewer
    /// calls to Discord and a longer wait before a revoked role takes effect.</panel>
    [LeafField("roleCacheTtlSec", "Role cache lifetime", Group = "session", Min = 0, Unit = "s")]
    public int RoleCacheTtlSeconds { get; set; } = 60;

    /// <summary>How long an in-flight authorize→callback handshake cookie stays valid.</summary>
    /// <panel>How long someone has to finish a sign-in once it starts before they have to begin again.</panel>
    [LeafField("stateTtlSec", "Sign-in window", Group = "session", Min = 30, Unit = "s")]
    public int StateTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Exact SPA origins (scheme + host, no trailing slash) allowed by CORS, and the same list a
    /// sign-in may return a browser to. One list rather than two: a client trusted to call this
    /// service with a bearer is exactly a client trusted to be handed one, and two lists would drift.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Resolves the URL a completed sign-in may send the browser back to, or refuses it. A redirect
    /// target that came in on a request is an open-redirect surface unless it is checked against a
    /// list the operator wrote, so this admits only an absolute http(s) URL whose <em>origin</em> is
    /// in <see cref="AllowedOrigins"/> — the path and query are the client's own business.
    /// <para>
    /// Any fragment on the candidate is dropped: the fragment is what carries the session back, so a
    /// caller-supplied one would be overwritten anyway, and silently keeping half of it is worse than
    /// dropping it outright.
    /// </para>
    /// </summary>
    public bool TryResolveReturnUrl(string? candidate, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        string origin = uri.GetLeftPart(UriPartial.Authority);
        bool listed = AllowedOrigins.Any(o =>
            !string.IsNullOrWhiteSpace(o) &&
            string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        if (!listed) return false;

        resolved = uri.GetLeftPart(UriPartial.Query);
        return true;
    }

    /// <summary>The token lifetimes and signing material, as the shared session package wants them.</summary>
    /// <remarks>
    /// <c>Issuer</c> is this surface's own and is passed explicitly rather than defaulted: it is
    /// validated on every token, so the value a host has already minted under is the value it must keep.
    /// </remarks>
    public SessionTokenOptions ToSessionTokenOptions() => new(
        ResolveHostId(),
        SigningKey,
        TimeSpan.FromSeconds(AccessTtlSeconds > 0 ? AccessTtlSeconds : 900),
        TimeSpan.FromSeconds(SessionTtlSeconds > 0 ? SessionTtlSeconds : 30 * 24 * 60 * 60),
        Issuer: "kgsm-assistant");
}
