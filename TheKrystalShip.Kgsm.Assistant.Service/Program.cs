using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Service;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;

var builder = WebApplication.CreateBuilder(args);

// The files declaring the service's whole configurable surface, shipped beside the binary. Resolved
// against the binary's own directory rather than the content root: under systemd those are not
// reliably the same place, and a relative path would start the service with none of its
// configuration instead of failing.
//
// Inserted at the FRONT of the source list, because this is the floor: a source added later wins,
// so everything above it — the unit's Environment=, /etc/kgsm-assistant/service.env, a command-line
// argument — still overrides one key of it, which is the whole point of declaring the surface here.
// Appending instead would let the file quietly beat the env file that is supposed to configure a host.
foreach (string file in new[]
         {
             $"kgsm-assistant.settings.{builder.Environment.EnvironmentName}.json",
             "kgsm-assistant.settings.json",
         })
{
    builder.Configuration.Sources.Insert(0, new JsonConfigurationSource
    {
        FileProvider = new PhysicalFileProvider(AppContext.BaseDirectory),
        Path = file,
        Optional = file.Contains(builder.Environment.EnvironmentName, StringComparison.Ordinal),
        ReloadOnChange = false,
    });
}

// Ecosystem-standard logging (see ../tks/logging-convention.md): one journald-native SystemdConsole
// sink (the <N> syslog priority prefix lets `journalctl -p` filter by level). CreateBuilder already
// binds the "Logging" appsettings section + env overrides; this swaps the default providers for the
// single Systemd sink. (The CLI/Eval entrypoints are interactive tools, not services, and deliberately
// keep a SimpleConsole→stderr setup instead — see the convention's "CLI variant".)
builder.Logging.ClearProviders();
builder.Logging.AddSystemdConsole();

// --- Options (web-only) ------------------------------------------------------
// The kgsm/inventory/web-search options moved to the Infrastructure library and are bound by
// AddKgsmAdapters below. These three stay here — they are web-host concerns (auth + webhook).
builder.Services.Configure<AssistantServiceOptions>(
    builder.Configuration.GetSection(AssistantServiceOptions.Section));
builder.Services.Configure<DiscordOAuthOptions>(
    builder.Configuration.GetSection(DiscordOAuthOptions.Section));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.Section));
// The ecosystem's shared authorization block. Bound once and the resolved map registered, because
// every caller wants the same answer and the map is immutable.
builder.Services.Configure<KgsmAuthOptions>(
    builder.Configuration.GetSection(KgsmAuthOptions.Section));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<KgsmAuthOptions>>().Value.ToRoleMap());

// --- LLM + assistant + kgsm adapters -----------------------------------------
// The reusable agent loop (Ollama client, conversation store) and the kgsm assistant
// (prompt builder with the lib's canonical prompt, tool dispatcher, policy, ConfirmAsync).
// No Llm:* config is required here — the prompt text lives in the library.
builder.Services.AddLocalLlm(builder.Configuration);
builder.Services.AddKgsmAssistant();
// The kgsm-lib graph + port adapters + Tavily web search, behind one socket-safe seam shared
// with the CLI. MUST come AFTER AddKgsmAssistant so the concrete IWebSearch (TavilyWebSearch)
// wins over the library's fail-closed DisabledWebSearch default. AddKgsmAdapters reads the KGSM,
// InventoryCache and WebSearch config sections.
builder.Services.AddKgsmAdapters(builder.Configuration);
// This host is resident, so it listens to the engine's events: a blueprint edited in the Control
// Panel or from another operator's CLI drops the blueprint cache here immediately, instead of the
// assistant answering from a stale catalog until the TTL expires. Reads only when
// KGSM:JournalDir is set (the CLI never sets it — see AddKgsmEventListener).
builder.Services.AddKgsmEventListener(builder.Configuration);
// The startup orphan sweep for create_blueprint test-install probes (plan step 10's backstop) — the
// first IHostedService in this repo. Runs once at startup and exits; see its own doc comment.
builder.Services.AddHostedService<BlueprintProbeSweepService>();

// --- Security ----------------------------------------------------------------
builder.Services.AddSingleton<ConfirmationTokenService>();
// write_file's confirmation-token carrier for a body too large for a stateless HMAC token (§ Contracts) —
// shares the conversation store's SQLite file (bound by AddLocalLlm above), adding its own table.
builder.Services.AddSingleton<IPendingWriteStore, SqlitePendingWriteStore>();

// --- Web auth (Discord OAuth) ------------------------------------------------
// A sign-in yields a short-lived access bearer plus a refresh token, both signed by this service
// and both carrying the session id the registry can revoke. The in-flight login handshake needs no
// server-side store at all: its state and PKCE verifier ride in one HttpOnly cookie on the browser
// doing the login.
//
// Lifetimes: the registry, the caches and the token service are SINGLETONS; the Discord directory is
// transient (its HttpClient is factory-managed); the orchestration service and the filters are
// SCOPED — so no singleton ever captures the transient client.
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();

builder.Services.AddSingleton<ISessionTokenService>(sp => new SessionTokenService(
    sp.GetRequiredService<IOptions<AuthOptions>>().Value.ToSessionTokenOptions(),
    sp.GetRequiredService<ILogger<SessionTokenService>>()));
builder.Services.AddSingleton<ISessionRegistry, SqliteSessionRegistry>();
builder.Services.AddMemoryCache();
// The cache TTL is the revocation lag for anything that cannot evict — a logout evicts, so in
// practice a kill is immediate and this is only the backstop.
builder.Services.AddSingleton<ISessionValidator>(sp => new SessionValidator(
    sp.GetRequiredService<ISessionRegistry>(),
    sp.GetRequiredService<IMemoryCache>(),
    TimeSpan.FromSeconds(5)));
builder.Services.AddHostedService(sp => new SessionCleanupWorker(
    sp.GetRequiredService<ISessionRegistry>(),
    TimeSpan.FromHours(1),
    sp.GetRequiredService<ILogger<SessionCleanupWorker>>()));
builder.Services.AddSingleton(new DiscordTierCache(
    TimeSpan.FromSeconds(authOptions.RoleCacheTtlSeconds > 0 ? authOptions.RoleCacheTtlSeconds : 60)));

// This surface's own OAuth endpoints; the application credentials, guild and role ids are the
// shared KgsmAuth block. The scopes are passed explicitly rather than left to the package default:
// what sign-in asks for shows up on the session, so it is this surface's to state.
builder.Services.AddSingleton(sp =>
{
    var discord = sp.GetRequiredService<IOptions<DiscordOAuthOptions>>().Value;
    return new DiscordOAuthEndpoints(discord.RedirectUri, discord.Scopes);
});
// DiscordDirectory takes the options class itself, so the bound value is registered bare as well as
// behind IOptions — without it the real directory cannot be constructed at all, and every test that
// substitutes the seam would still pass.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KgsmAuthOptions>>().Value);
builder.Services.AddHttpClient<IDiscordDirectory, DiscordDirectory>(
    c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddScoped<DiscordAuthService>();
builder.Services.AddScoped<BearerAuthFilter>();
builder.Services.AddScoped<AdminOnlyFilter>();

// CORS: allow the configured SPA origin to call with an Authorization header. NO
// AllowCredentials (bearer, not cookies). UseCors is ordered before the secured group so
// cross-origin preflight (OPTIONS) is answered by the CORS middleware, pre-auth.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(authOptions.AllowedOrigins)
    .WithMethods("GET", "POST")
    .WithHeaders("Authorization", "Content-Type")));

var app = builder.Build();

app.UseCors();

// Warn loudly if actions are switched on but can't actually be authorized — the service
// then stays read-only (CanPerformActionsAsync requires all of these), the safe default.
{
    var opts = app.Services.GetRequiredService<IOptions<AssistantServiceOptions>>().Value;
    var tokens = app.Services.GetRequiredService<ConfirmationTokenService>();
    var sharedAuth = app.Services.GetRequiredService<IOptions<KgsmAuthOptions>>().Value;
    var roleMap = app.Services.GetRequiredService<KgsmRoleMap>();
    if (opts.ActionsEnabled && !tokens.IsConfigured)
        app.Logger.LogWarning(
            "Assistant:ActionsEnabled is true but Assistant:Confirmation:Key is unset — " +
            "the service will run READ-ONLY until a key is configured.");
    if (opts.ActionsEnabled &&
        (string.IsNullOrEmpty(sharedAuth.ClientSecret) || !sharedAuth.CanResolveRoles || roleMap.IsEmpty))
        app.Logger.LogWarning(
            "Assistant:ActionsEnabled is true but KgsmAuth is not fully configured " +
            "(ClientSecret/BotToken/GuildId/RoleOperatorIds) — direct SESSION-bearer callers can't be " +
            "authorized for actions. The trusted relay (kgsm-api) path is unaffected: it uses the " +
            "api's verified tier, not a Discord lookup.");
    else if (opts.ActionsEnabled)
        app.Logger.LogInformation(
            "Authorization: {OperatorCount} operator role(s), {AdminCount} admin role(s); " +
            "guild members floor at viewer",
            roleMap.OperatorRoleIds.Count, roleMap.AdminRoleIds.Count);

    // The bot token resolves guild membership AND roles (the caller's OAuth token is discarded
    // after /users/@me), so without it no login can succeed at all.
    if (string.IsNullOrEmpty(sharedAuth.BotToken) && !string.IsNullOrEmpty(sharedAuth.GuildId))
        app.Logger.LogWarning(
            "KgsmAuth:BotToken is unset — guild-membership and role lookups use the bot " +
            "token, so every login will be denied until it is configured.");
}

// The surface this service's conversation ids are namespaced under: web:{userId}[:{chatId}]. Every
// key is composed from it, and the review surface is scoped to it — a conversation from another
// surface in the same database is not this service's to serve.
const string WebSurface = "web";

// --- Public endpoints --------------------------------------------------------
// Open: a liveness probe, the two auth-bootstrap endpoints (a caller has no session yet) and the
// refresh, which is authenticated by the refresh token in its own body rather than by a bearer.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The in-flight login cookie — written at /start, consumed at /callback. It carries BOTH halves of
// the handshake: the CSRF `state` Discord echoes back, and the PKCE `code_verifier` presented at the
// exchange. HttpOnly and this origin's, so only the browser that began the login can satisfy it —
// that binding IS the CSRF property, and a set of issued states held server-side would not have it.
// SameSite=Lax, never Strict: Strict suppresses the cookie on the top-level redirect back from
// Discord, which breaks every login.
const string StateCookie = "kgsm_oauth_state";

CookieOptions StateCookieOptions() => new()
{
    HttpOnly = true,
    Secure = !app.Environment.IsDevelopment(),
    SameSite = SameSiteMode.Lax,
    Path = "/auth",
    MaxAge = TimeSpan.FromSeconds(authOptions.StateTtlSeconds > 0 ? authOptions.StateTtlSeconds : 300),
};

// Begin the OAuth bounce — 302 to Discord (this service owns the client id, redirect and scopes).
// `prompt=none` is silent SSO; a client retries with `consent` when Discord answers login_required.
app.MapGet("/auth/discord/start", (HttpContext http, DiscordAuthService auth, [FromQuery] string? prompt) =>
{
    var handshake = OAuthHandshake.Create();
    http.Response.Cookies.Append(StateCookie, handshake.ToCookieValue(), StateCookieOptions());
    // Only the challenge travels — the verifier stays in the cookie, never in a URL.
    return Results.Redirect(auth.BuildAuthorizeUrl(handshake, prompt));
});

// The OAuth landing — verify the state, exchange the code, resolve the tier, mint the session.
//   200 { verdict:"ok", tier, token, refresh, … }   authorized
//   403 { verdict:"denied", … }                     identity verified, no role on this host (terminal)
//   400 forged/stale state or a missing code · 401 bad or expired code · 502 Discord unreachable
app.MapGet("/auth/discord/callback", async (
    HttpContext http,
    DiscordAuthService auth,
    ILoggerFactory loggerFactory,
    [FromQuery] string? code,
    [FromQuery] string? state,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("DiscordCallback");

    // CSRF gate, before any exchange: the state Discord echoed back must equal the one issued to THIS
    // browser. The cookie is one-time — cleared whatever the outcome, so a callback cannot be replayed.
    // A missing cookie (expired, or a login that never started here), a malformed one, or a mismatch
    // is a forged or stale login, and the answer is 400 rather than any kind of grant.
    string? cookie = http.Request.Cookies[StateCookie];
    if (cookie is not null)
        http.Response.Cookies.Delete(StateCookie, StateCookieOptions());
    if (!OAuthHandshake.TryParse(cookie, out OAuthHandshake handshake) || !handshake.MatchesState(state))
        return Results.BadRequest(new { error = "invalid_state", message = "the sign-in did not validate — start again." });

    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "bad_request", message = "missing authorization code" });

    ResolvedPrincipal? resolved;
    try
    {
        resolved = await auth.ResolveAsync(code, handshake.CodeVerifier, ct);
    }
    catch (DiscordAuthException ex)
    {
        // Could not reach or parse Discord — an honest upstream failure, NEVER a default grant.
        logger.LogWarning(ex, "Discord auth exchange failed.");
        return Results.Json(
            new { error = "auth_provider_error", message = "Could not complete authentication with Discord." },
            statusCode: StatusCodes.Status502BadGateway);
    }

    if (resolved is null)
        return Results.Json(
            new { error = "login_required", message = "The authorization code was invalid or expired." },
            statusCode: StatusCodes.Status401Unauthorized);

    // Identity verified, but no access on this host. Terminal: no tokens are minted, and a client
    // must not treat it as something a retry could fix.
    if (resolved.Tier == KgsmTier.None)
        return Results.Json(
            new AuthSessionResponse("denied", null, null, null, null, null,
                resolved.Identity.UserId, resolved.Identity.Display),
            statusCode: StatusCodes.Status403Forbidden);

    string? userAgent = http.Request.Headers.UserAgent.ToString();
    if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

    AuthSessionResult session = await auth.CreateSessionAsync(resolved, userAgent, ct);
    return Results.Ok(new AuthSessionResponse(
        "ok", KgsmTiers.ToWire(session.Tier), session.AccessToken, session.RefreshToken,
        session.AccessExpires, session.RefreshExpires, session.UserId, session.DisplayName));
});

// Trade a refresh token for a fresh pair. Unauthenticated by bearer on purpose — the whole point is
// to be callable once the access token has lapsed; the refresh token is the credential. A rotated-away
// or revoked token is refused rather than renewed.
app.MapPost("/auth/session/refresh", async (RefreshRequest request, DiscordAuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Refresh))
        return Results.BadRequest(new { error = "bad_request", message = "a refresh token is required." });

    AuthSessionResult? session = await auth.RefreshAsync(request.Refresh, ct);
    return session is null
        ? Results.Unauthorized()
        : Results.Ok(new AuthSessionResponse(
            "ok", KgsmTiers.ToWire(session.Tier), session.AccessToken, session.RefreshToken,
            session.AccessExpires, session.RefreshExpires, session.UserId, session.DisplayName));
});

app.MapPost("/events", async (
    HttpRequest httpRequest,
    IInventoryInvalidation inventory,
    IOptions<AssistantServiceOptions> options,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("KgsmWebhook");

    using var buffer = new MemoryStream();
    await httpRequest.Body.CopyToAsync(buffer, ct);
    var body = buffer.ToArray();

    var secret = options.Value.Webhook.Secret;
    if (!string.IsNullOrEmpty(secret))
    {
        var signature = httpRequest.Headers["X-KGSM-Signature"].ToString();
        if (!KgsmWebhookSignature.Verify(signature, body, secret))
        {
            logger.LogWarning("Rejected kgsm webhook: invalid or missing signature");
            return Results.Unauthorized();
        }
    }
    else
    {
        logger.LogWarning("Webhook secret not configured — signature is NOT enforced");
    }

    // The payload's only job here is cache invalidation; any instance lifecycle event can
    // change the inventory, so invalidate unconditionally. Parse only to log the type.
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("EventType", out var eventType))
        {
            string? type = eventType.GetString();
            logger.LogInformation("kgsm event received: {EventType}", type);
            // A blueprint_* event is the only kind where the change is NOT to a server's runtime
            // state but to a blueprint FILE on disk — a category the inventory also caches (the
            // blueprint catalog) and needs to drop. The typed line makes a web-originated blueprint
            // edit's cache-bust visible to operators skimming journalctl for "why did the assistant
            // see the new values". A host wired to the event socket invalidates on the same events
            // through KgsmEventListener; the two paths are redundant on purpose, since which
            // transport kgsm uses is the engine's configuration to make, not this service's.
            if (!string.IsNullOrEmpty(type) && type.StartsWith("blueprint_", StringComparison.Ordinal))
                logger.LogInformation("blueprint event from kgsm: {EventType} — invalidating blueprint inventory", type);
        }
    }
    catch (JsonException)
    {
        logger.LogWarning("kgsm webhook body was not valid JSON; invalidating cache anyway");
    }

    inventory.Invalidate();
    return Results.NoContent();
});

// --- Secured endpoints -------------------------------------------------------
// Every user endpoint requires a valid session whose principal is a guild member (login +
// membership for ALL — mirrors having to be in the Discord server to use the bot). The
// action role additionally gates mutations, computed FRESH per call.
var secured = app.MapGroup("").AddEndpointFilter<BearerAuthFilter>();

// Who am I, and may I act right now? Lets the SPA show/hide action affordances.
secured.MapGet("/auth/me", async (HttpContext http, DiscordAuthService auth, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    // The tier is re-derived, not read off the bearer: a role granted or taken away since sign-in is
    // already in effect for every action, so reporting the token's snapshot here would tell a client
    // something the next request would contradict.
    var tier = await auth.ResolveTierAsync(principal, ct);
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    return Results.Ok(new MeResponse(
        principal.UserId, principal.DisplayName, KgsmTiers.ToWire(tier), canPerform));
});

// The tools the caller is authorized to use, with names/descriptions/parameters.
// Fully server-derived — no client input. Lets the SPA populate a tool picker.
secured.MapGet("/tools", async (HttpContext http, DiscordAuthService auth, IPromptOverrides promptOverrides,
    IOptions<SearchOptions> searchOptions, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);

    var tools = canPerform ? LlmTools.All : LlmTools.ReadOnly;
    // Mirror ServerAssistant.SelectTools: omit `search` when no source backs it (§D7), so the SPA's
    // picker never lists a tool the turn would reject.
    if (!searchOptions.Value.Available)
        tools = tools.Where(t => t.Tool != LlmTools.Search).ToArray();
    tools = promptOverrides.OverlayTools(tools);

    var dtos = tools.Select(t => new ToolDto(
        t.Name,
        t.Description,
        t.Parameters.Select(p => new ToolParameterDto(
            p.Name, p.Description, p.Required, p.Type, p.AllowedValues)).ToArray())).ToArray();

    return Results.Ok(dtos);
});

// The caller's own past chats (the reverse path): list every conversation under their server-derived
// memory namespace web:{userId}, so a fresh browser/device can show history that lives server-side, not
// only in the client. Principal-scoped — a caller can only ever see ITS OWN conversations.
secured.MapGet("/conversations", (HttpContext http, IConversationStore store) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var conversations = store.ListConversations($"{WebSurface}:{principal.UserId}")
        .Select(s => ConversationHistoryMapper.ToSummaryDto(s, principal.UserId))
        .ToArray();
    return Results.Ok(conversations);
});

// One past chat's full transcript (turns + non-destructive compaction checkpoints), oldest-first, so the
// client renders the WHOLE history as it happened. The key is composed exactly as /turn does — the
// server-derived user-id prefix + the sanitised per-chat id — so {id} can only ever address the caller's
// OWN conversation. An unknown id ⇒ an empty transcript (still 200), never another user's data.
secured.MapGet("/conversations/{id}", (string id, HttpContext http, IConversationStore store) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";
    var entries = store.GetHistory(conversationId)
        .Select(ConversationHistoryMapper.ToEntryDto)
        .ToArray();
    return Results.Ok(new ConversationHistoryDto(chatScope ?? string.Empty, entries));
});

// Soft-delete one of the caller's chats: hides it from their list while keeping the full transcript in the
// append-only history (the self-improvement corpus is never destroyed). The key is composed exactly as the
// reads above — the server-derived user-id prefix + the sanitised per-chat id — so {id} can only ever
// address the caller's OWN conversation. Idempotent; a later turn on the same id (a resume) un-hides it.
secured.MapDelete("/conversations/{id}", (string id, HttpContext http, IConversationStore store) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";
    store.SoftDelete(conversationId);
    return Results.NoContent();
});

// How the caller judged one of their OWN answers — the only signal in the corpus that says whether an
// answer was any good, as opposed to how it ran. Deliberately on the user-facing group, not the review
// one: this is satisfaction, written by the person the answer was for, and a reviewer's opinion of
// someone else's conversation is a different fact that the read-only review surface does not collect.
// The key is composed exactly as the reads above, and the store additionally verifies the turn belongs
// to it, so neither the route nor the id can reach another user's turn.
secured.MapPost("/conversations/{id}/turns/{turnId:long}/feedback", (
    string id, long turnId, TurnFeedbackRequest request, HttpContext http, IConversationStore store) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";

    // A null rating withdraws a verdict already left; anything else must name one of the two.
    TurnFeedbackRating? rating = request.Rating?.ToLowerInvariant() switch
    {
        "up" => TurnFeedbackRating.Up,
        "down" => TurnFeedbackRating.Down,
        null or "" => null,
        _ => (TurnFeedbackRating?)(-1),
    };
    if (rating == (TurnFeedbackRating)(-1))
        return Results.BadRequest(new { error = "rating must be 'up', 'down', or null." });

    // A note explains a thumbs-down. Keeping one on a thumbs-up would record a complaint against an
    // answer the person said was fine, which is not a thing any reader could act on.
    var note = rating == TurnFeedbackRating.Down ? request.Note : null;

    return store.SetTurnFeedback(conversationId, turnId, rating, note)
        ? Results.NoContent()
        : Results.NotFound(new { error = "unknown turn." });
});

// Compact one of the caller's chats on demand: summarise its history in place to free up the context
// window, returning a CompactionOutcome. The key is composed exactly as the reads/delete above — the
// server-derived user-id prefix + the sanitised per-chat id — so {id} can only ever address the caller's
// OWN conversation. Non-destructive (a checkpoint is appended; the append-only transcript is preserved) and
// idempotent-ish: a conversation with too little history to be worth a model round-trip returns
// Compacted=false, untouched. A model/upstream failure ⇒ 502; the stored history is left as-is.
secured.MapPost("/conversations/{id}/compact", async (
    string id, HttpContext http, IConversationCompactor compactor, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";

    var result = await compactor.CompactAsync(conversationId, ct);
    if (result.IsFailure)
        return Results.Problem(result.Error, statusCode: StatusCodes.Status502BadGateway);

    var outcome = result.Value!;
    return Results.Ok(new CompactionResultDto(outcome.Compacted, outcome.MessagesCompacted, outcome.Summary));
});

// --- The review surface: reading OTHER users' conversations ------------------
// Admin-gated (AdminOnlyFilter: the api's forwarded decision on the relay path, the caller's own
// Discord review role on the session-bearer path — so the leaf's review surface works standalone).
// Read-only by construction: there is no admin write here, and nothing below can reach a
// conversation outside the web surface. Chats users have "deleted" ARE listed, flagged — the
// transcript was never erased, and a hidden conversation is exactly what a tuning review wants.
var review = app.MapGroup("/admin")
    .AddEndpointFilter<BearerAuthFilter>()
    .AddEndpointFilter<AdminOnlyFilter>();

// Everyone who has talked to this assistant, derived from the conversation ids themselves (the store
// holds no user registry). The list a reviewer picks from.
review.MapGet("/conversations/users", (IConversationStore store) =>
{
    var users = store.ListActors(WebSurface)
        .Select(a => new AdminConversationUserDto(
            a.UserId, a.UserDisplay, a.ConversationCount, a.DeletedCount, a.TurnCount,
            a.FirstActivityAt, a.LastActivityAt))
        .ToArray();
    return Results.Ok(users);
});

// The whole-corpus roll-up: outcome mix, answer times, per-tool behaviour, prompt-version buckets and
// daily volume, alongside what the assistant is configured to be right now. Derived from the same log
// the transcripts come from, so a figure here can never disagree with the turns behind it.
review.MapGet("/conversations/stats", (
    IConversationStore store,
    IOptions<OllamaOptions> ollama,
    IOptions<LlmAgentOptions> agent,
    IOptions<AssistantServiceOptions> assistant) =>
{
    var stats = store.GetStats(WebSurface);

    // Whether a recorded tool name is one this assistant actually ships is a question only the
    // catalog can answer, and the catalog lives here — the store that counted the calls is
    // domain-blind by design and reports the name it found either way. Checked against EveryToolName,
    // not the ordinary-turn offer: a conditionally-offered tool (revise_blueprint) is real, and
    // reporting it as invented would send a reviewer chasing a bug that isn't there.
    var tools = stats.Tools
        .Select(t => new AdminToolStatDto(
            t.Name, LlmTools.EveryToolName.Contains(new Tool(t.Name)),
            t.Calls, t.MedianMs, t.MaxMs, t.FailedCalls))
        .ToArray();

    return Results.Ok(new AdminConversationStatsDto(
        stats.Conversations, stats.DeletedConversations, stats.Actors, stats.Turns,
        stats.OkTurns, stats.ErrorTurns, stats.CapHitTurns, stats.CancelledTurns,
        stats.UnrecordedOutcomeTurns,
        stats.MedianTurnMs, stats.P95TurnMs, stats.MaxTurnMs,
        stats.MedianIterations, stats.MaxIterations,
        stats.MedianContextPercent, stats.MaxContextPercent, stats.ContextWindow,
        stats.ThinkingTurns, stats.TurnsWithoutTool, stats.ToolCalls,
        tools,
        stats.PromptVersions
            .Select(p => new AdminPromptVersionDto(
                p.Hash, p.Turns, p.OkTurns, p.MedianMs, p.NegativeTurns, p.RatedTurns)).ToArray(),
        stats.Activity.Select(a => new AdminDailyTurnsDto(a.Date, a.Turns)).ToArray(),
        new AdminAssistantRuntimeDto(
            ollama.Value.Model, ollama.Value.NumCtx, agent.Value.MaxIterations,
            assistant.Value.ActionsEnabled),
        stats.RatedTurns, stats.PositiveTurns, stats.NegativeTurns, stats.SatisfactionPercent,
        // The store reports the raw stored id; the handle a reviewer can actually open one by is minted
        // here, where the review surface's addressing lives.
        stats.FeedbackNotes
            .Select(n => new AdminFeedbackNoteDto(
                ReviewConversationId.Encode(n.ConversationId), n.TurnId, n.Note, n.Prompt, n.At))
            .ToArray()));
});

// One user's conversations. The user id comes from the list above, so it names an EXISTING namespace;
// an unknown one is simply an empty list, never an error (nothing is revealed either way).
review.MapGet("/conversations", (string user, IConversationStore store) =>
{
    if (string.IsNullOrWhiteSpace(user))
        return Results.BadRequest(new { error = "user is required." });

    var conversations = store.ListConversations($"{WebSurface}:{user}", includeDeleted: true)
        .Select(ReviewConversationId.ToDto)
        .ToArray();
    return Results.Ok(conversations);
});

// One conversation's transcript, addressed by the opaque handle the listing minted. The entries are
// the SAME shape GET /conversations/{id} returns for your own chat, so a client renders a reviewed
// transcript through its existing path. A handle that doesn't decode, or decodes outside the web
// surface, is a 404 — the surface only serves what it lists.
review.MapGet("/conversations/{id}", (string id, IConversationStore store) =>
{
    if (!ReviewConversationId.TryDecode(id, WebSurface, out var conversationId))
        return Results.NotFound(new { error = "unknown conversation." });

    var userId = ReviewConversationId.UserOf(conversationId, WebSurface);
    var summary = store.ListConversations($"{WebSurface}:{userId}", includeDeleted: true)
        .FirstOrDefault(s => s.ConversationId == conversationId);
    if (summary is null)
        return Results.NotFound(new { error = "unknown conversation." });

    var entries = store.GetHistory(conversationId)
        .Select(ConversationHistoryMapper.ToEntryDto)
        .ToArray();

    return Results.Ok(new AdminConversationHistoryDto(
        new AdminConversationUserRefDto(userId, summary.UserDisplay),
        ReviewConversationId.ToDto(summary),
        entries));
});

// Sign out: revoke the session so the bearer stops working at once, rather than staying
// cryptographically valid until it expires.
secured.MapPost("/auth/logout", async (HttpContext http, DiscordAuthService auth, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    await auth.LogoutAsync(principal, ct);
    return Results.NoContent();
});

secured.MapPost("/turn", async (
    TurnRequest request,
    HttpContext http,
    IServerAssistant assistant,
    ConfirmationTokenService tokens,
    IPendingWriteStore pendingWrites,
    IOptions<AssistantServiceOptions> assistantOptions,
    DiscordAuthService auth,
    IInvocationContext invocation,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
        return Results.BadRequest(new { error = "prompt is required." });

    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    // Whether THIS turn may propose actions.
    //  - trusted relay (kgsm-api): the caller's verified tier, forwarded as X-Relay-Tier and stashed by
    //    BearerAuthFilter. Proposing is an operator capability and needs no toggle — the user still
    //    confirms each proposal. A relay host may have no Discord config of its own, so the forwarded
    //    tier is the only correct source; this service adds only its local preconditions (actions
    //    enabled + a Confirmation signing key to mint the proposal token).
    //  - direct session bearer: the caller's own Discord tier, ANDed with their per-turn toggle.
    // autoExecute = auto-accept: on a relayed turn the api ALSO forwards its admin-tier ∧ toggle
    // decision (X-Relay-Auto-Act). When set, the dispatcher RUNS lifecycle commands immediately instead
    // of staging them. It is gated to canPerform so the propose-gate (BuildGate) always allows what
    // auto-execute then runs; the direct-bearer path never auto-executes (propose-only).
    bool canPerform;
    bool autoExecute = false;
    if (http.Items.TryGetValue(BearerAuthFilter.RelayTierKey, out var relayObj) && relayObj is KgsmTier relayTier)
    {
        var asstOpts = http.RequestServices.GetRequiredService<IOptions<AssistantServiceOptions>>().Value;
        canPerform = relayTier >= KgsmTier.Operator && asstOpts.ActionsEnabled && tokens.IsConfigured;
        var relayAutoAct = http.Items.TryGetValue(BearerAuthFilter.RelayAutoActKey, out var autoObj)
            && autoObj is bool b && b;
        autoExecute = canPerform && relayAutoAct;
    }
    else
    {
        canPerform = (request.Actions ?? false) && await auth.CanPerformActionsAsync(principal, ct);
    }
    var think = request.Think
        ?? http.RequestServices.GetRequiredService<IOptions<OllamaOptions>>().Value.Think;

    // The conversation (memory) key. ALWAYS namespaced under the server-derived user id, so one user can
    // never read or poison another's history. An optional per-CHAT sub-id partitions THIS user's own
    // memory into separate conversations — each "new chat" in the SPA becomes a fresh context window. It
    // arrives on the trusted-relay path as X-Relay-Conversation-Id (stashed by BearerAuthFilter) and on
    // the direct session path in the request body. Sanitised here (the authority that builds the key);
    // absent/blank ⇒ the bare per-user key, unchanged for clients that don't send a chat id.
    var chatScope = ConversationScope.Sanitize(
        http.Items.TryGetValue(BearerAuthFilter.RelayConversationIdKey, out var relayConv) && relayConv is string rc
            ? rc
            : request.ConversationId);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";

    // Attribute any server mutation this turn runs to the asking user (origin=assistant); flows down the
    // awaited turn → tool dispatch → kgsm chokepoint. Covers both the SSE and buffered paths below.
    using var provenance = invocation.Begin(Invocation.ForAssistant(principal.DisplayName));

    // Opt into token streaming with `Accept: text/event-stream`; everyone else gets the buffered
    // JSON contract unchanged. (SSE here is POST, so the SPA reads it via fetch()+ReadableStream —
    // the browser EventSource is GET-only and can't carry the bearer.)
    var wantsStream = http.Request.Headers.Accept
        .Any(v => v is not null && v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));

    if (wantsStream)
    {
        await SseTurnWriter.WriteAsync(
            http, assistant, tokens, pendingWrites, assistantOptions.Value.Confirmation.TtlSeconds,
            principal, conversationId, request.Prompt, canPerform, think, autoExecute, request.Tools, request.DraftYaml);
        return Results.Empty;
    }

    var result = await assistant.RunAsync(conversationId, request.Prompt, canPerform, think, autoExecute, request.Tools, ct, request.DraftYaml, principal.DisplayName);

    if (result.IsFailure)
    {
        // Distinguish client input errors (400) from upstream failures (502).
        var isInvalidTool = result.Error?.StartsWith("Invalid tool") == true;
        return Results.Problem(
            result.Error,
            statusCode: isInvalidTool
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status502BadGateway);
    }

    var confirmations = result.Confirmations
        .Select(c =>
        {
            // write_file: swap the real content for an opaque pending-write id BEFORE minting the
            // token (a 10 MB body can't ride a stateless HMAC token) — every other kind is untouched.
            var forToken = PendingWriteTokenSwap.ForToken(c, pendingWrites, assistantOptions.Value.Confirmation.TtlSeconds);
            return new ConfirmationDto(
                forToken.Kind.ToString().ToLowerInvariant(), forToken.Target, forToken.InstanceName,
                tokens.Create(forToken, principal.UserId), forToken.ConfigKey, forToken.ConfigValue);
        })
        .ToArray();

    return Results.Ok(new TurnResponse(result.Text, confirmations, UsageDto.From(result.Usage)));
});

secured.MapPost("/confirm", async (
    ConfirmRequest request,
    HttpContext http,
    IServerAssistant assistant,
    ConfirmationTokenService tokens,
    IPendingWriteStore pendingWrites,
    DiscordAuthService auth,
    IInvocationContext invocation,
    IOptions<AssistantServiceOptions> assistantOptions,
    CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    // Reject a malformed/expired token AND a token staged by a different user — with the same
    // generic message, so it isn't an oracle for which case occurred.
    if (!tokens.TryValidate(request.Token, out var confirmation, out var stagedBy) ||
        !string.Equals(stagedBy, principal.UserId, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid or expired confirmation." });

    // Re-derive authority FRESH at confirm time — never trust it from the token. Mirror the /turn path
    // exactly (the confirm EXECUTES a mutation, so it must read authority the SAME way the propose did):
    // on the trusted-relay path the caller's verified tier arrives as X-Relay-Tier, which is the only
    // correct source for a relay host with no Discord config of its own; a direct session bearer falls
    // back to its own Discord lookup.
    bool canPerform;
    if (http.Items.TryGetValue(BearerAuthFilter.RelayTierKey, out var relayObj) && relayObj is KgsmTier relayTier)
        canPerform = relayTier >= KgsmTier.Operator && assistantOptions.Value.ActionsEnabled && tokens.IsConfigured;
    else
        canPerform = await auth.CanPerformActionsAsync(principal, ct);
    // The confirming user is the authority for the action they just approved (origin=assistant).
    using var provenance = invocation.Begin(Invocation.ForAssistant(principal.DisplayName));

    // Blueprint finalize: the user reviewed/edited the draft in the chat. The edited YAML rides the
    // request body (re-validated downstream); a save without edits falls back to the staged draft (the
    // token's ConfigValue is its opaque pending-write id). The result is a rich card, not a text line —
    // and on a DraftReady outcome (repair exhausted / invalid edit) a FRESH Blueprint token is minted so
    // the user can edit and save again (the re-edit loop).
    if (confirmation.Kind == ConfirmationKind.Blueprint)
    {
        var game = confirmation.InstanceName ?? confirmation.Target;
        var editedYaml = request.EditedContent;
        if (string.IsNullOrWhiteSpace(editedYaml))
        {
            if (confirmation.ConfigValue is null || !pendingWrites.TryTake(confirmation.ConfigValue, out var stagedDraft))
                return Results.BadRequest(new { error = "This draft has expired — ask the assistant to draft it again." });
            editedYaml = stagedDraft;
        }

        // A finalize is minutes of test-install → verify → repair with long silent stretches. Buffered into
        // one response, that silence lets an idle-connection reaper on a remote path drop the socket, leaving
        // the chat card spinning with no terminal result. A caller that opts into `Accept: text/event-stream`
        // (the SPA, via the api relay) gets it STREAMED instead: progress steps + keep-alive heartbeats keep
        // the socket warm, and a terminal `result` frame carries the same ConfirmResponse. Everyone else
        // (CLI, a plain JSON caller) keeps the buffered contract below. Token/authority/draft were all resolved
        // above, so the SSE path only ever commits 200 after a clean validation.
        var wantsStream = http.Request.Headers.Accept
            .Any(v => v is not null && v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
        if (wantsStream)
        {
            await SseConfirmWriter.WriteAsync(
                http, assistant, http.RequestServices.GetRequiredService<ITurnProgress>(),
                tokens, pendingWrites, assistantOptions.Value.Confirmation.TtlSeconds,
                principal, game, editedYaml!, canPerform);
            return Results.Empty;
        }

        var outcome = await assistant.FinalizeBlueprintAsync(game, editedYaml!, canPerform, ct);
        var data = outcome.Data;

        // On DraftReady, re-stage the returned draft so the user can edit + save again. Mint a fresh token
        // over it (the draft body swapped into the pending-write store, same as the initial stage).
        ConfirmationDto[]? reEdit = null;
        if (data is not null && data.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
        {
            var restaged = PendingWriteTokenSwap.ForToken(
                new PendingConfirmation(ConfirmationKind.Blueprint, data.BlueprintName ?? game,
                    InstanceName: game, ConfigValue: data.DraftYaml),
                pendingWrites, assistantOptions.Value.Confirmation.TtlSeconds);
            reEdit =
            [
                new ConfirmationDto(
                    restaged.Kind.ToString().ToLowerInvariant(), restaged.Target, restaged.InstanceName,
                    tokens.Create(restaged, principal.UserId), restaged.ConfigKey, restaged.ConfigValue),
            ];
        }

        var card = JsonSerializer.SerializeToElement(ToolResultCard.From(outcome), SseTurnWriter.Json);
        var verified = data?.Outcome == BlueprintAuthoringOutcome.Verified;
        return Results.Ok(new ConfirmResponse(outcome.Summary, verified, card, reEdit));
    }

    // write_file: the token's ConfigValue is the opaque pending-write id (never the real content —
    // see PendingWriteTokenSwap), so rehydrate the real content here, single-use. An expired/already-
    // used id is an honest failure — never silently treated as an empty write.
    if (confirmation.Kind == ConfirmationKind.WriteFile)
    {
        if (confirmation.ConfigValue is null || !pendingWrites.TryTake(confirmation.ConfigValue, out var realContent))
            return Results.BadRequest(new { error = "This file write has expired or was already confirmed — ask the assistant to propose it again." });

        confirmation = confirmation with { ConfigValue = realContent };
    }

    var result = await assistant.ConfirmAsync(confirmation, canPerform, ct);

    return Results.Ok(new ConfirmResponse(
        result.IsSuccess ? result.Value! : result.Error!, result.IsSuccess));
});

app.Run();

/// <summary>Exposed so the test project's <c>WebApplicationFactory</c> can boot the app.</summary>
public partial class Program;
