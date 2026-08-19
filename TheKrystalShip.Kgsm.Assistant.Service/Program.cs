using System.Globalization;
using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Lifecycle;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Push;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Kgsm.Assistant.Service.Speech;
using TheKrystalShip.Kgsm.Assistant.Service.Streaming;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.WebPush;
using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Backends;

// Writing the command manifest is a build step, not a service: the build runs the binary it just
// produced so the shipped file is generated from this build's own catalog. Handled before anything
// else is composed — it needs no configuration, no Ollama and no kgsm.
if (args is ["--emit-commands", string manifestPath])
{
    CommandManifest.WriteTo(manifestPath);
    return;
}

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
// The ecosystem's shared authorization block. Only the application is read from it here: this
// surface signs people in through Discord, and what they may do afterwards comes from their KGSM
// account. The guild and role ids in that same file are kgsm-bot's.
builder.Services.Configure<KgsmAuthOptions>(
    builder.Configuration.GetSection(KgsmAuthOptions.Section));

// --- LLM + assistant + kgsm adapters -----------------------------------------
// The reusable agent loop (Ollama client, conversation store) and the kgsm assistant
// (prompt builder with the lib's canonical prompt, tool dispatcher, policy, ConfirmAsync).
// No Llm:* config is required here — the prompt text lives in the library.
builder.Services.AddLocalLlm(builder.Configuration);
// The conversation database is this service's state, and its directory is the unit's
// StateDirectory=kgsm-assistant — read back from $STATE_DIRECTORY so the unit is the only place the
// location is declared. Done here rather than in the library because the CLI shares that options
// class and has no state directory of its own. All three stores that open the file
// (SqliteConversationStore, SqlitePendingConfirmationStore, SqliteSessionRegistry) read the options
// object, so resolving it once here is what keeps them on the same file.
builder.Services.PostConfigure<ConversationOptions>(options =>
    options.DatabasePath = StatePaths.Resolve(
        options.DatabasePath, StatePaths.DefaultConversationDbPath, StatePaths.ConversationDbFileName));
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

// --- This leaf's own journal -------------------------------------------------
// The WRITE half. AddKgsmAdapters above registers the federated READ of every producer's journal,
// which is a different thing: that one answers questions about the fleet, this one records what this
// leaf has to say about itself.
//
// ⚠ Registered HERE and not in AddKgsmAdapters, which the CLI, the Eval harness and several test
// fixtures also compose. A writer there would put a ready/stopping pair in the live journal on every
// `kgsm-assistant-cli` invocation and hundreds of turn-quality lines in it on every eval run.
//
// No state root is named: the default places it under the unit's StateDirectory=kgsm-assistant, which
// is where a reader scanning for producers looks. The version comes off THIS assembly — the
// deployable that carries <Version> — because a class library carries none and would stamp every line
// with a version no release was ever numbered.
builder.Services.AddKgsmJournal(
    AssistantJournal.Producer, typeof(AssistantLifecycleReporter).Assembly);

// What this leaf says about its own state, as opposed to what it records about the people using it.
// Its own recorder: these lines are this process reporting on itself with nobody behind them, where
// everything else the assistant journals carries the person whose turn it was.
builder.Services.AddSingleton(sp => new LeafLifecycle(
    sp.GetRequiredService<IEventJournalWriter>(),
    sp.GetRequiredService<ILogger<LeafLifecycle>>()));

builder.Services.AddSingleton<AssistantJournal>();
builder.Services.AddSingleton<IAssistantJournal>(sp => sp.GetRequiredService<AssistantJournal>());
builder.Services.AddHostedService<AssistantLifecycleReporter>();

// The model backend, measured by being used rather than pinged — connecting to a socket-activated
// model is what loads it, so a probe on a timer would pin its VRAM resident forever. Decorates the one
// call that actually reaches the model, so a tool that threw or a turn somebody stopped is never
// reported as a dead backend. Wraps whichever ILlmClient AddLocalLlm registered above.
ServiceDescriptor? llmClient = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ILlmClient));
if (llmClient?.ImplementationType is { } backend)
{
    builder.Services.Remove(llmClient);
    builder.Services.AddSingleton(backend);
    builder.Services.AddSingleton<ILlmClient>(sp => new MeasuredLlmClient(
        (ILlmClient)sp.GetRequiredService(backend), sp.GetRequiredService<LeafLifecycle>()));
}

// --- Security ----------------------------------------------------------------
// Every action the assistant proposes is held here and surfaced to the client as an opaque handle;
// what would be done never leaves this process. Shares the conversation store's SQLite file (bound
// by AddLocalLlm above), adding its own table.
builder.Services.AddSingleton<IPendingConfirmationStore, SqlitePendingConfirmationStore>();

// Fan-out of a person's own conversation changes to their own open streams (GET /events), so a chat
// held in the Control Panel and in the installed app agrees with itself without either polling. In
// memory on purpose: a change nobody was connected for is not owed to anyone, because every surface
// re-reads the listing when its stream opens.
builder.Services.AddSingleton<IConversationEventBus, ConversationEventBus>();

// The running turns. A turn is a session with its own lifetime rather than work owned by the request
// that asked for it — which is what lets a second surface watch one, lets any of them stop it, and
// lets it survive the surface that started it going away. Also in memory: a turn that was interrupted
// by a restart is over, and nothing here is owed durability the conversation store does not give.
// Reading an answer aloud, and hearing one asked. The null ones are registered FIRST and
// unconditionally, so an assistant on a host with no speech leaf has working ports that report
// themselves unavailable — the same fail-closed shape as DisabledRetrieval. The real adapters replace
// them only when this host is configured to use the engine, and even then they answer "unavailable"
// until the leaf's socket is actually there.
//
// One switch covers both directions because one leaf serves both: a host either has kgsm-speech or it
// does not, and a surface that could be heard but not answered aloud is a configuration nobody wants.
builder.Services.AddSingleton<ISpokenAudio, NoSpokenAudio>();
builder.Services.AddSingleton<ISpokenWords, NoSpokenWords>();
if (builder.Configuration.GetValue("Speech:Enabled", true))
{
    builder.Services.AddSingleton<ISpokenAudio>(sp => new LeafSpokenAudio(
        builder.Configuration["Speech:SocketPath"],
        sp.GetRequiredService<ILogger<LeafSpokenAudio>>()));
    builder.Services.AddSingleton<ISpokenWords>(sp => new LeafSpokenWords(
        builder.Configuration["Speech:SocketPath"],
        sp.GetRequiredService<IServerInventory>(),
        sp.GetRequiredService<ILogger<LeafSpokenWords>>()));
}

builder.Services.AddSingleton<ITurnRegistry, TurnRegistry>();
builder.Services.AddHostedService<TurnPresenceWorker>();

// --- Web Push ----------------------------------------------------------------
// What reaches somebody who asked for an action and then put the phone down. This leaf owns the whole
// path — its own VAPID pair, its own devices, its own staged buttons — because a confirmation is the
// assistant's to announce and it must keep working when kgsm-api is not running. The shared package is
// the protocol and nothing else.
//
// Both stores sit on the same SQLite file as everything else here, each creating its own tables.
builder.Services.AddSingleton<IPushActionStore, SqlitePushActionStore>();
builder.Services.AddSingleton<IPushSubscriptionStore, SqlitePushSubscriptionStore>();
// A typed client so the push services' connections are pooled and the handler recycles; the sender
// itself holds no state and no ILogger — every failure comes back as a result its caller logs.
builder.Services.AddHttpClient<WebPushSender>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHostedService<ConfirmationPushWorker>();
// Runs an action approved from a notification detached from the request that carried the tap,
// and pushes the verdict back — a service worker cannot be held open for a fifteen-minute backup.
builder.Services.AddSingleton<PushConfirmationRunner>();

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
builder.Services.AddSingleton(new KgsmTierCache(
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
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<KgsmAuthOptions>>().Value.For(KgsmActorProvider.Discord));
// Discord answers ONE half of the sign-in here: who someone is. Transient like the typed client it
// wraps — holding one in a singleton pins a handler for the process lifetime and stops the factory
// rotating it.
builder.Services.AddHttpClient<DiscordDirectory>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddTransient<IIdentityProvider>(sp => sp.GetRequiredService<DiscordDirectory>());

// This host's own KGSM accounts, read straight off the shared store file. A singleton because it
// wraps one SQLite file every request reads; it opens connections per operation and holds none.
builder.Services.AddSingleton<UserDirectory>();

// The other half: what a verified caller may DO comes from the account store and nowhere else,
// whichever provider vouched for them. A guild role is a fact about a chat server, not about this
// host — so a Discord login and a password login are answered from the same record, and a person
// holds the same tier here as in the Control Panel because both read that record rather than each
// deriving one. This surface re-derives per request, so a change made in the panel lands without
// anyone signing in again.
builder.Services.AddTransient<IAuthorityProvider>(sp =>
    new AccountAuthority(sp.GetRequiredService<UserDirectory>()));
builder.Services.AddTransient<ISignInService>(sp => new SignInService(
    sp.GetRequiredService<IIdentityProvider>(),
    sp.GetRequiredService<IAuthorityProvider>()));

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BearerAuthFilter>();
builder.Services.AddScoped<AdminOnlyFilter>();

// The header a surface names its own event stream with, so the changes it causes come back stamped
// and it can skip re-applying what it already did. Optional everywhere: a caller that sends none is
// simply not distinguished from any other, which costs it one redundant re-read.
const string OriginHeaderName = "X-Assistant-Origin";

// CORS: allow the configured SPA origins to call with an Authorization header. NO
// AllowCredentials (bearer, not cookies). UseCors is ordered before the secured group so
// cross-origin preflight (OPTIONS) is answered by the CORS middleware, pre-auth.
//
// DELETE is here because a client owns its conversations and deleting one is a DELETE; without it a
// cross-origin client can hold a whole chat history it has no way to remove. The list is the verbs
// this service actually answers on — not a wildcard.
//
// X-Assistant-Origin is how a surface names its own event stream. Without it on this list the
// preflight refuses the header and a cross-origin surface silently loses the ability to recognise its
// own echoes — it would re-apply everything it just did.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(authOptions.AllowedOrigins)
    .WithMethods("GET", "POST", "DELETE")
    .WithHeaders("Authorization", "Content-Type", OriginHeaderName)));

var app = builder.Build();

// The prompts and tool definitions are files, installed beside the service. Prove they are there and
// coherent HERE, at startup, rather than on the first question: a tool the catalog and the dispatcher
// disagree about fails the turn it is called on, and a missing prompt segment does not look like a
// fault at all — it looks like the assistant having changed its mind about what it is.
try
{
    var promptsDir = AssistantTextCheck.Validate(app.Services);
    app.Logger.LogInformation("Assistant text: prompts and tool definitions read from {Directory}", promptsDir);
}
catch (AssistantTextUnavailableException ex)
{
    // Exit rather than throw: systemd reports a clean failure code and the journal carries the one
    // line that says what to fix, instead of an unhandled-exception dump around it.
    app.Logger.LogCritical("Assistant text unusable: {Reason}", ex.Message);
    Environment.Exit(1);
    return;
}

app.UseCors();

// The assistant's own web client, when one is installed: wwwroot/ under the content root
// (/opt/kgsm-assistant/service/wwwroot on a deployed host), published by kgsm-web's
// deploy/deploy-assistant.sh. Absent on a host that serves no UI, and this is then a no-op.
//
// It is registered BEFORE the endpoints and shadows none of them: static middleware serves only
// files that exist on disk, so /turn, /confirm, /auth/… and /conversations fall straight through —
// none of them names a file. The client is hash-routed, so serving index.html at / is the whole of
// what it needs; there is no SPA fallback to add and therefore no rule that could swallow an
// unmatched API path and turn a typo into a 200.
app.UseDefaultFiles();
app.UseStaticFiles();

// Say what can actually authorize anyone, once, at startup — the two failures below are both silent
// otherwise, and both look to a user like the assistant simply refusing to do anything.
{
    var opts = app.Services.GetRequiredService<IOptions<AssistantServiceOptions>>().Value;
    var sharedAuth = app.Services.GetRequiredService<IOptions<KgsmAuthOptions>>().Value;
    var accounts = app.Services.GetRequiredService<UserDirectory>();

    if (!accounts.Available)
        app.Logger.LogError(
            "The KGSM account store is unavailable ({Reason}) — nobody can be authorized for anything " +
            "until it can be read, and every authenticated request answers 502 meanwhile.",
            accounts.UnavailableReason);

    if (!sharedAuth.For(KgsmActorProvider.Discord).Configured)
        app.Logger.LogInformation(
            "No Discord application is configured — a KGSM password is the way in. Signing in through " +
            "Discord needs KgsmAuth:Providers:discord:ClientId and :ClientSecret.");

    if (opts.ActionsEnabled)
        app.Logger.LogInformation(
            "Actions are enabled: a caller holding the operator tier on their KGSM account can run them.");
}

// The surface this service's conversation ids are namespaced under: web:{userId}[:{chatId}]. Every
// key is composed from it, and the review surface is scoped to it — a conversation from another
// surface in the same database is not this service's to serve.
const string WebSurface = ConversationSurfaces.Web;

// Conversations keyed to a PLACE instead of a person live under room:{room} — see
// ConversationSurfaces, which composes every key and is where the difference is explained. A room
// carries no user segment, which is what makes it shared and why only a permitted leaf may name one
// (RelayLeaves.OpensRooms): a room exists to whoever is speaking in it, and is addressed by speaking
// there rather than by naming an id. Nothing a person addresses BY id — their chat list, a
// transcript — reaches one.

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

// Where a completed sign-in sends the browser back to, when one was asked for. It rides its own
// cookie because Discord echoes back only `state` — there is nowhere else for it to travel, and the
// handshake cookie stays exactly the two secrets it is named for. Same discipline as that cookie:
// HttpOnly, this origin's, one-time, and short-lived.
const string ReturnCookie = "kgsm_oauth_return";

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
//
// `return_to` is a browser client asking to be sent back to itself with the session, instead of
// receiving the JSON a programmatic caller gets. It is checked HERE against Auth:AllowedOrigins and
// refused outright — bouncing to Discord for a login that cannot be completed wastes the user's
// consent and turns a config mistake into a confusing dead end at the callback.
app.MapGet("/auth/discord/start", (
    HttpContext http, AuthService auth, [FromQuery] string? prompt, [FromQuery(Name = "return_to")] string? returnTo) =>
{
    if (!string.IsNullOrWhiteSpace(returnTo))
    {
        if (!authOptions.TryResolveReturnUrl(returnTo, out string resolvedReturn))
            return Results.BadRequest(new
            {
                error = "invalid_return_to",
                message = "that return address is not an allowed origin on this host",
            });

        http.Response.Cookies.Append(ReturnCookie, resolvedReturn, StateCookieOptions());
    }

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
    AuthService auth,
    UserDirectory users,
    ILoggerFactory loggerFactory,
    [FromQuery] string? code,
    [FromQuery] string? state,
    // Discord's own refusal, when it declines to authorize instead of returning a code. A silent
    // sign-in (prompt=none) that needs a human answers `consent_required`/`login_required` here.
    [FromQuery] string? error,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("DiscordCallback");

    // The return address this login asked for, if any — read and cleared alongside the state cookie so
    // an error is delivered the same way a success would have been, rather than dead-ending a browser
    // on a JSON body. Re-validated here and not merely trusted from the cookie: a cookie is client-held
    // and carries no integrity of its own, so the allowlist is checked at both ends of the round trip.
    string? returnCookie = http.Request.Cookies[ReturnCookie];
    if (returnCookie is not null)
        http.Response.Cookies.Delete(ReturnCookie, StateCookieOptions());
    string? returnUrl = authOptions.TryResolveReturnUrl(returnCookie, out string checkedReturn) ? checkedReturn : null;

    // One exit for every outcome: a browser that asked to be returned gets a 302 carrying the result in
    // the URL fragment (never the query — a fragment is not sent to the server, kept in Referer, or
    // written to an access log), and a programmatic caller gets the JSON it always got.
    IResult Finish(string fragment, Func<IResult> json) =>
        returnUrl is null ? json() : Results.Redirect($"{returnUrl}#{fragment}");

    static string Frag(params (string Key, string? Value)[] parts) =>
        string.Join("&", parts
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));

    // CSRF gate, before any exchange: the state Discord echoed back must equal the one issued to THIS
    // browser. The cookie is one-time — cleared whatever the outcome, so a callback cannot be replayed.
    // A missing cookie (expired, or a login that never started here), a malformed one, or a mismatch
    // is a forged or stale login, and the answer is 400 rather than any kind of grant.
    string? cookie = http.Request.Cookies[StateCookie];
    if (cookie is not null)
        http.Response.Cookies.Delete(StateCookie, StateCookieOptions());
    if (!OAuthHandshake.TryParse(cookie, out OAuthHandshake handshake) || !handshake.MatchesState(state))
        return Finish(Frag(("error", "invalid_state")), () => Results.BadRequest(
            new { error = "invalid_state", message = "the sign-in did not validate — start again." }));

    // Discord declined rather than issuing a code. Its own reason is carried through verbatim: a
    // silent sign-in that needs a human says `consent_required`, and a client that cannot tell that
    // apart from a malformed callback has to guess between retrying visibly and giving up. Reported
    // only after the state check, because an error response carries `state` too and a forged one
    // must not be echoed back to a caller as though this service had spoken to Discord about it.
    if (!string.IsNullOrWhiteSpace(error))
    {
        logger.LogInformation("Discord declined the authorization: {Error}", error);
        return Finish(Frag(("error", error)), () => Results.Json(
            new { error, message = "Discord declined the authorization." },
            statusCode: StatusCodes.Status401Unauthorized));
    }

    if (string.IsNullOrWhiteSpace(code))
        return Finish(Frag(("error", "bad_request")), () => Results.BadRequest(
            new { error = "bad_request", message = "missing authorization code" }));

    ResolvedPrincipal? resolved;
    try
    {
        resolved = await auth.ResolveAsync(code, handshake.CodeVerifier, ct);
    }
    catch (DiscordAuthException ex)
    {
        // Could not reach or parse Discord — an honest upstream failure, NEVER a default grant.
        logger.LogWarning(ex, "Discord auth exchange failed.");
        return Finish(Frag(("error", "auth_provider_error")), () => Results.Json(
            new { error = "auth_provider_error", message = "Could not complete authentication with Discord." },
            statusCode: StatusCodes.Status502BadGateway));
    }

    if (resolved is null)
        return Finish(Frag(("error", "login_required")), () => Results.Json(
            new { error = "login_required", message = "The authorization code was invalid or expired." },
            statusCode: StatusCodes.Status401Unauthorized));

    // A verified identity is not yet a user. It proves an account here or it proves none, and when it
    // proves none an unapproved one is created for it to prove — at no tier, so the session it gets
    // can say who it is and nothing else. Guild membership is not consulted and buys nothing.
    if (!users.Available)
        return Finish(Frag(("error", "authority_unavailable")), () => Results.Json(
            new { error = "authority_unavailable", message = users.UnavailableReason ?? "The KGSM account store is unavailable on this host." },
            statusCode: StatusCodes.Status502BadGateway));

    LinkResult link;
    try
    {
        link = await users.Linking.ResolveOrProvisionAsync(
            resolved.Identity, DateTimeOffset.UtcNow, users.Pending, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not resolve {Handle} against the KGSM account store.",
            resolved.Identity.Handle);
        return Finish(Frag(("error", "authority_unavailable")), () => Results.Json(
            new { error = "authority_unavailable", message = "The KGSM account store could not be read." },
            statusCode: StatusCodes.Status502BadGateway));
    }

    if (link.Outcome == LinkOutcome.PendingCapReached)
    {
        // Not a denial of this person — a refusal to hold more unapproved accounts, which from the
        // outside is indistinguishable from one, so it is logged where an admin will see it.
        logger.LogWarning(
            "{Handle} signed in but this host is already holding {Cap} accounts awaiting approval.",
            resolved.Identity.Handle, users.Pending.Cap);
        return Finish(Frag(("error", "not_accepting_accounts")), () => Results.Json(
            new { error = "not_accepting_accounts", message = "This host is not accepting new accounts right now." },
            statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    // Identity verified, and this host has switched the account off. Terminal: no tokens are minted,
    // and a client must not treat it as something a retry could fix.
    if (link.User!.Status == UserStatus.Disabled)
        return Finish(Frag(("error", "denied")), () => Results.Json(
            new AuthSessionResponse("denied", null, null, null, null, null,
                resolved.Identity.Subject, resolved.Identity.Display),
            statusCode: StatusCodes.Status403Forbidden));

    // An unapproved account signs in and holds nothing — a real session at tier none, so a surface can
    // say "awaiting approval" instead of showing somebody who just proved who they are a bare denial.
    resolved = resolved with { Tier = link.User.EffectiveTier };

    string? userAgent = http.Request.Headers.UserAgent.ToString();
    if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

    AuthSessionResult session = await auth.CreateSessionAsync(resolved, userAgent, ct);
    // `access` and `refresh` are the key names kgsm-api already hands back, so a client that reads one
    // node's return leg reads this one with the same code. `tier` is additive — it saves the client a
    // round trip it would otherwise make immediately.
    return Finish(
        Frag(("access", session.AccessToken), ("refresh", session.RefreshToken),
             ("tier", KgsmTiers.ToWire(session.Tier))),
        () => Results.Ok(new AuthSessionResponse(
            "ok", KgsmTiers.ToWire(session.Tier), session.AccessToken, session.RefreshToken,
            session.AccessExpires, session.RefreshExpires, session.UserId, session.DisplayName)));
});

// Sign in with a KGSM password. The door that needs no identity provider configured on this host at
// all — and the one an admin created at setup comes through. Unauthenticated by bearer on purpose:
// it is what somebody without a session reaches for.
//
// An unknown username and a wrong password give one answer at one cost. Two answers is a username
// oracle, and so is a faster one; the store spends a hash verification either way.
app.MapPost("/auth/login", async (
    LoginRequest request, UserDirectory users, AuthService auth, HttpContext http,
    ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    if (!users.Available)
        return Results.Json(
            new { error = "users_unavailable", message = users.UnavailableReason ?? "The KGSM account store is unavailable on this host." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "bad_request", message = "a username and a password are required." });

    DateTimeOffset now = DateTimeOffset.UtcNow;
    LocalSignInResult result;
    try
    {
        result = await users.SignIn.SignInAsync(request.Username, request.Password, now, ct);
    }
    catch (Exception ex)
    {
        // The store went away between startup and now. An outage, never a denial — the same rule as
        // an unreachable identity provider.
        loggerFactory.CreateLogger("LocalLogin").LogError(ex, "The KGSM account store could not be read.");
        return Results.Json(
            new { error = "users_unavailable", message = "The KGSM account store could not be read." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    switch (result.Outcome)
    {
        case LocalSignInOutcome.InvalidCredentials:
            // Logged with the username that was tried, so an operator can still see a run of attempts
            // against one name — the distinction the wire deliberately does not carry.
            loggerFactory.CreateLogger("LocalLogin")
                .LogInformation("Sign-in refused for '{Username}'.", request.Username);
            return Results.Json(
                new { error = "invalid_credentials", message = "That username and password do not match an account here." },
                statusCode: StatusCodes.Status401Unauthorized);

        case LocalSignInOutcome.LockedOut:
            int seconds = Math.Max(1, (int)Math.Ceiling(((result.RetryAfter ?? now) - now).TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Results.Json(
                new { error = "too_many_attempts", message = $"Too many failed attempts. Try again in {seconds}s." },
                statusCode: StatusCodes.Status429TooManyRequests);

        case LocalSignInOutcome.Disabled:
            return Results.Json(
                new { error = "account_disabled", message = "That account is disabled on this host." },
                statusCode: StatusCodes.Status403Forbidden);
    }

    // A pending account signs in and holds nothing — a real session at tier none, so a surface can
    // say "awaiting approval" instead of showing somebody who just proved who they are a bare denial.
    ResolvedPrincipal principal = result.Principal!;

    string? loginUserAgent = http.Request.Headers.UserAgent.ToString();
    if (string.IsNullOrWhiteSpace(loginUserAgent)) loginUserAgent = null;

    AuthSessionResult signedIn = await auth.CreateSessionAsync(principal, loginUserAgent, ct);
    return Results.Ok(new AuthSessionResponse(
        "ok", KgsmTiers.ToWire(signedIn.Tier), signedIn.AccessToken, signedIn.RefreshToken,
        signedIn.AccessExpires, signedIn.RefreshExpires, signedIn.UserId, signedIn.DisplayName));
});

// Trade a refresh token for a fresh pair. Unauthenticated by bearer on purpose — the whole point is
// to be callable once the access token has lapsed; the refresh token is the credential. A rotated-away
// or revoked token is refused rather than renewed.
app.MapPost("/auth/session/refresh", async (RefreshRequest request, AuthService auth, CancellationToken ct) =>
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

// The caller's own conversation changes, pushed as they happen: what the switches now stand at, a
// conversation started or deleted, a log that grew. Principal-scoped — a stream only ever carries its
// own caller's changes — and content-free beyond the switches, so a transcript is still read from the
// endpoint that owns it rather than mirrored down a second, drifting path.
//
// A surface that names itself with X-Assistant-Origin gets its own changes back stamped with that id
// and skips them, having already applied what it asked for.
secured.MapGet("/events", async (
    HttpContext http, IConversationEventBus bus, ISessionValidator sessions, ITurnRegistry turns) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    await SseConversationWriter.WriteAsync(
        http, bus, sessions, turns, principal, $"{WebSurface}:{principal.UserId}");
    return Results.Empty;
});

// Who am I, and may I act right now? Lets the SPA show/hide action affordances.
secured.MapGet("/auth/me", async (
    HttpContext http, AuthService auth, UserDirectory users, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    // The tier is re-derived, not read off the bearer: authority granted or taken away since sign-in
    // is already in effect for every action, so reporting the token's snapshot here would tell a
    // client something the next request would contradict. An unresolvable authority floors to none,
    // which reads the chat down to a viewer for as long as the store is unreadable rather than
    // failing the boot the whole dock hangs off.
    var tier = (await auth.ResolveTierAsync(principal, ct)).OrNone;
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    // Two different facts wear the same `none`: somebody waiting on an admin, and somebody this host
    // does not know. Reported as unknown rather than guessed at when the store cannot be read.
    string status = "unknown";
    if (users.Available)
    {
        try
        {
            if ((await users.Authority!.ResolveAsync(principal.AsIdentity(), ct)).User is { } account)
                status = UserStatuses.ToWire(account.Status);
        }
        catch (KgsmAuthProviderException) { /* unknown, as above */ }
    }
    return Results.Ok(new MeResponse(
        principal.UserId, principal.DisplayName, KgsmTiers.ToWire(tier), canPerform, status));
});

// The tools the caller is authorized to use, with names/descriptions/parameters. Fully server-derived
// — no client input. Lets the SPA populate a tool picker, and backs the /tools chat command.
static async Task<ToolDto[]> AuthorizedToolsAsync(
    AuthPrincipal principal, AuthService auth, IToolCatalog catalog,
    IOptions<SearchOptions> searchOptions, CancellationToken ct)
{
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);

    var tools = canPerform ? catalog.All : catalog.ReadOnly;
    // Mirror ServerAssistant.SelectTools: omit `search` when no source backs it (§D7), so the SPA's
    // picker never lists a tool the turn would reject.
    if (!searchOptions.Value.Available)
        tools = tools.Where(t => t.Tool != catalog.NameOf(LlmTools.Search)).ToArray();

    return [.. tools.Select(t => new ToolDto(
        t.Name,
        t.Description,
        [.. t.Parameters.Select(p => new ToolParameterDto(
            p.Name, p.Description, p.Required, p.Type, p.AllowedValues))]))];
}

secured.MapGet("/tools", async (HttpContext http, AuthService auth, IToolCatalog catalog,
    IOptions<SearchOptions> searchOptions, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    return Results.Ok(await AuthorizedToolsAsync(principal, auth, catalog, searchOptions, ct));
});

// Three minutes of 16kHz mono 16-bit audio, which is the ceiling a recording is refused above.
const int MaxUtteranceBytes = 3 * 60 * 16000 * 2;

// Whether this host can hear and whether it can speak, asked before a surface offers a microphone or
// a Read-aloud toggle. Both are one leaf, so both answers move together — but they are reported
// separately because a surface acts on them separately, and reporting one derived from the other
// would be this service holding an opinion about a leaf it only talks to.
//
// It is a question about the HOST, not the caller, and it is cheap: the socket is bound by systemd
// whether or not the daemon is running, so this starts nothing and loads nothing. Secured all the
// same — everything else on this leaf is, and what is installed on a host is not public.
secured.MapGet("/speech", (ISpokenAudio audio, ISpokenWords words) =>
    Results.Ok(new SpeechResponse(words.Available, audio.Available)));

// One utterance in, the words in it out. The audio is 16kHz mono signed 16-bit PCM — whisper's native
// input, which the browser has already resampled to, because doing it there costs one pass over a
// buffer the browser already holds decoded and doing it here would need an audio codec in this
// service. Raw rather than a container: the sample rate is the contract, and a header restating it is
// a second place for the two to disagree.
//
// The transcript is returned, never sent. What somebody says into a microphone is a draft until they
// look at it — recognition is wrong often enough that a surface which turned a voice note straight
// into a turn would ask the assistant things nobody said.
secured.MapPost("/transcribe", async (HttpContext http, ISpokenWords words, CancellationToken ct) =>
{
    if (!words.Available)
        return Results.Json(
            new { error = "This host has no speech engine, so it cannot transcribe anything." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    // Read with a hard ceiling rather than trusting Content-Length, which is a claim. The cap is about
    // three minutes of audio: long enough for anything anybody dictates at a chat box, short enough
    // that one note cannot hold the recogniser — a single pass at a time — against everyone else.
    using var buffer = new MemoryStream();
    await http.Request.Body.CopyToAsync(buffer, ct);
    byte[] pcm = buffer.ToArray();

    if (pcm.Length == 0)
        return Results.BadRequest(new { error = "No audio was sent." });
    if (pcm.Length > MaxUtteranceBytes)
        return Results.BadRequest(new { error = "That recording is too long — keep it under three minutes." });
    // A 16-bit sample is two bytes, so an odd length is not the format this endpoint documents. It
    // would still transcribe, off by half a sample for the whole run, which is worse than refusing.
    if (pcm.Length % 2 != 0)
        return Results.BadRequest(new { error = "The audio is not 16-bit samples." });

    string? heard = await words.HearAsync(pcm, ct);

    // Null is the pass not happening; an empty string is a pass that ran and found nothing. Told
    // apart, because "we could not listen" and "you did not say anything" send somebody to different
    // places — and a recording of a quiet room is a real thing to have made.
    if (heard is null)
        return Results.Json(
            new { error = "The speech engine could not read that recording." },
            statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new TranscriptResponse(heard));
});

// The commands this caller may type at the assistant, filtered to their tier — a command above it is
// absent rather than listed and refused, so a surface never offers what it would then reject. The
// shipped manifest the Control Panel renders carries the same catalog UNfiltered: a live surface shows
// a person what they can type, a descriptive file documents the leaf.
secured.MapGet("/commands", async (HttpContext http, AuthService auth, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var tier = (await auth.ResolveTierAsync(principal, ct)).OrNone;
    return Results.Ok(ChatCommands.For(tier).Select(CommandDto.From).ToArray());
});

// Run one command. The leaf performs every command it lists — nothing here is a client-side convention
// this service is unaware of, which is what lets a surface treat GET /commands as authoritative rather
// than advisory. Unknown names 404 rather than falling through to the model: this endpoint is not a
// second way to ask a question, and answering one here would hide a client bug.
secured.MapPost("/commands/{name}", async (
    string name,
    CommandRequest? request,
    HttpContext http,
    AuthService auth,
    IConversationStore conversations,
    IConversationCompactor compactor,
    IToolCatalog catalog,
    IMemoryStore memories,
    IConversationEventBus bus,
    IOptions<SearchOptions> searchOptions,
    IOptions<LlmBackendOptions> llmOptions,
    CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var origin = http.Request.Headers.TryGetValue(OriginHeaderName, out var named) && named.Count > 0
        ? named[0]
        : null;

    var command = ChatCommands.Find(name);
    if (command is null)
        return Results.NotFound(new { error = $"There is no /{name} command." });

    // The same gate GET /commands filters on, re-checked here rather than trusted from the listing: a
    // client can post any name it likes, and the listing is a convenience, never the authorization.
    var tier = (await auth.ResolveTierAsync(principal, ct)).OrNone;
    if (tier < command.Gate)
        return Results.Json(
            new { error = $"/{command.Name} needs {KgsmTiers.ToWire(command.Gate)}." },
            statusCode: StatusCodes.Status403Forbidden);

    if (!ChatCommands.TryReadState(request?.Argument, out var requested))
        return Results.BadRequest(new
        {
            error = $"/{command.Name} takes {ChatCommands.On} or {ChatCommands.Off}, "
                  + "or nothing at all to toggle it.",
        });

    var chatScope = ConversationScope.Sanitize(request?.ConversationId);

    // Resolved exactly as /turn resolves it, so a command acts on the conversation the next thing said
    // will continue. Composed per-endpoint, this is where a room asking to be compacted quietly folded
    // the caller's own chat instead and reported success.
    var room = ConversationSurfaces.RoomOf(http);
    var conversationId = ConversationSurfaces.Key(http, principal.UserId, chatScope);

    // Tell the caller's OTHER surfaces where the switches now stand, re-read rather than assembled
    // from what was just written — the frame and a later listing must not be able to disagree.
    void PublishSwitches()
    {
        var standing = conversations.GetPreferences(conversationId);
        bus.Publish(principal.UserId, new ConversationEvent(
            ConversationStream.Switches,
            new SwitchesChanged(
                chatScope ?? string.Empty, origin,
                standing.Think ?? llmOptions.Value.Think,
                standing.Autorun ?? false)));
    }

    switch (command.Name)
    {
        case "help":
            return Results.Ok(new CommandResultDto(
                command.Name,
                "Here's what you can type here.",
                Commands: [.. ChatCommands.For(tier).Select(CommandDto.From)]));

        case "tools":
            return Results.Ok(new CommandResultDto(
                command.Name,
                "Here's what I can do for you.",
                Tools: await AuthorizedToolsAsync(principal, auth, catalog, searchOptions, ct)));

        case "memory":
        {
            // Resolved through the same key the turn uses, so typing this in a room shows what that
            // ROOM remembers rather than what the person asking does.
            var owner = MemoryScope.OwnerOf(conversationId);
            var remembered = memories.List(owner).Select(MemoryDto.From).ToArray();
            return Results.Ok(new CommandResultDto(
                command.Name,
                remembered.Length == 0
                    ? "I haven't written anything down yet."
                    : $"Here's what I remember ({remembered.Length}).",
                Memories: remembered));
        }

        case "new":
        {
            // A ROOM has nowhere to start over TO. Its id is derived from the place it happens in, so
            // the next thing said there resolves back to the same key however many ids are minted —
            // starting fresh can only mean the conversation itself starting fresh, which is a reset:
            // the room stops replaying anything from before this moment, and the transcript keeps it
            // all.
            if (room is not null)
            {
                // ⚠ Gated above where the same command is free in a private chat. Clearing a room is
                // one person acting on a conversation everybody there is holding, and the people it
                // takes the memory from are not the person who asked.
                if (tier < KgsmTier.Operator)
                    return Results.Json(
                        new { error = $"Clearing a shared conversation needs {KgsmTiers.ToWire(KgsmTier.Operator)}." },
                        statusCode: StatusCodes.Status403Forbidden);

                conversations.Reset(conversationId);
                return Results.Ok(new CommandResultDto(
                    command.Name, "Cleared this conversation. I've forgotten what we were talking about."));
            }

            // The id is the client's to offer (it is what the next turn will carry), but the
            // conversation is the leaf's to create — so a fresh chat exists, lists, and is resumable
            // from another device the moment it is started rather than only once it is spoken into.
            if (string.IsNullOrEmpty(chatScope))
                return Results.BadRequest(new { error = "/new needs the id of the conversation to start." });

            // The offered id is taken up only while it holds nothing — a surface that minted one and is
            // asking for it to be brought into being. Typed INTO a conversation that has been spoken in,
            // "start a fresh conversation" has to mean a different one, so the leaf names it and the
            // surface follows: the answer always says which conversation is now the fresh one.
            var started = conversations.GetHistory(conversationId).Count == 0
                ? chatScope
                : Guid.NewGuid().ToString("N");

            conversations.CreateConversation($"{WebSurface}:{principal.UserId}:{started}");
            bus.Publish(principal.UserId, new ConversationEvent(
                ConversationStream.Started, new ConversationChanged(started, origin)));
            return Results.Ok(new CommandResultDto(
                command.Name, "Started a fresh conversation.", ConversationId: started));
        }

        case "compact":
        {
            var result = await compactor.CompactAsync(conversationId, ct);
            if (result.IsFailure)
                return Results.Problem(result.Error, statusCode: StatusCodes.Status502BadGateway);

            var outcome = result.Value!;
            var n = outcome.MessagesCompacted;
            // A checkpoint is a transcript change, so the caller's other surfaces re-read it.
            if (outcome.Compacted)
                bus.Publish(principal.UserId, new ConversationEvent(
                    ConversationStream.Activity, new ConversationChanged(chatScope ?? string.Empty, origin)));
            return Results.Ok(new CommandResultDto(
                command.Name,
                outcome.Compacted
                    ? $"Compacted {n} message{(n == 1 ? "" : "s")} into a summary."
                    : "Nothing to compact yet.",
                Compaction: new CompactionResultDto(outcome.Compacted, n, outcome.Summary)));
        }

        case "think":
        {
            var standing = conversations.GetPreferences(conversationId).Think ?? llmOptions.Value.Think;
            var next = requested ?? !standing;
            conversations.SetPreferences(conversationId, new ConversationPreferences(next, null));
            PublishSwitches();
            return Results.Ok(new CommandResultDto(
                command.Name,
                next
                    ? "Thinking on — I'll reason it through before answering."
                    : "Thinking off — I'll answer directly.",
                State: next));
        }

        case "autorun":
        {
            var standing = conversations.GetPreferences(conversationId).Autorun ?? false;
            var next = requested ?? !standing;
            conversations.SetPreferences(conversationId, new ConversationPreferences(null, next));
            PublishSwitches();
            return Results.Ok(new CommandResultDto(
                command.Name,
                next
                    ? "Auto-run on — I'll carry out authorized actions in this conversation without asking."
                    : "Auto-run off — I'll ask you to confirm each action.",
                State: next));
        }

        // Every catalog entry is handled above. Reaching here means a command was added to the catalog
        // and not given a body, which is a bug in this file — reported as one rather than as a 404,
        // which would say the command does not exist when the catalog says it does.
        default:
            return Results.Problem(
                $"/{command.Name} is listed but not implemented.",
                statusCode: StatusCodes.Status501NotImplemented);
    }
});

// What the assistant remembers about the caller: reading it, writing one by hand, and dropping one.
//
// ⚠ The owner is resolved through ConversationSurfaces.Key + MemoryScope, never composed inline as
// web:{userId}. In a room these must address the ROOM's memory — composing it per-endpoint is exactly
// how compacting a room quietly folded the caller's own chat and reported success.
//
// Memory is personal, so all of them are viewer-gated like the conversation reads beside them: seeing,
// correcting and deleting what is remembered about YOU needs no authority over any server.
secured.MapGet("/memories", (HttpContext http, IMemoryStore memories) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var owner = MemoryScope.OwnerOf(ConversationSurfaces.Key(http, principal.UserId, chatScope: null));
    return Results.Ok(memories.List(owner).Select(MemoryDto.From).ToArray());
});

// The bounds a surface writes within, so an editor's counters are read from the host rather than
// restated in a client that cannot know when they change.
secured.MapGet("/memories/limits", (IOptions<MemoryOptions> options) =>
    Results.Ok(new MemoryLimitsDto(
        options.Value.MaxPerOwner, options.Value.MaxSummaryLength, options.Value.MaxBodyLength)));

// Writing one by hand. Create and correct are the same call because that is what the store does: a
// memory is revised by rewriting its key, so an edit verb here would be inventing a second mechanism
// for the one the model already uses.
//
// Every refusal names the limit it hit and what to do about it, in the same terms the remember tool
// refuses the model with — a person correcting a memory is owed the same sentence the assistant gets.
secured.MapPut("/memories/{key}", (
    string key, MemoryWriteRequest request, HttpContext http,
    IMemoryStore memories, IOptions<MemoryOptions> options) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var owner = MemoryScope.OwnerOf(ConversationSurfaces.Key(http, principal.UserId, chatScope: null));

    // Sanitised exactly as the tool sanitises what the model writes, and idempotently, so a key read
    // out of the listing rewrites the row it was shown rather than filing a near-duplicate beside it.
    var sanitized = MemoryKey.Sanitize(key);
    if (sanitized is null)
        return Results.BadRequest(new { error = "A memory needs a name made of letters and numbers." });

    var summary = request.Summary?.Trim();
    if (string.IsNullOrWhiteSpace(summary))
        return Results.BadRequest(new
        {
            error = "A memory needs a summary — the one line the assistant reads back. State the fact "
                  + "itself, not that a note exists.",
        });

    var limits = options.Value;
    if (summary.Length > limits.MaxSummaryLength)
        return Results.BadRequest(new
        {
            error = $"That summary is {summary.Length} characters and the limit is "
                  + $"{limits.MaxSummaryLength}. Shorten it to the fact itself and put the detail in the body.",
        });

    var body = request.Body?.Trim() ?? string.Empty;
    if (body.Length > limits.MaxBodyLength)
        return Results.BadRequest(new
        {
            error = $"That body is {body.Length} characters and the limit is {limits.MaxBodyLength}. "
                  + "Keep what matters and drop the rest.",
        });

    // ⚠ Origin null, which is what the record reserves for a memory a person entered themselves. It
    // applies to a correction too: once somebody has rewritten the sentence, it is theirs and no
    // longer an account of what some conversation concluded.
    var record = new MemoryRecord(sanitized, summary, body, DateTimeOffset.UtcNow, Origin: null);

    // ⚠ A refusal here is only ever "this would be a NEW memory past the cap" — the store allows a
    // rewrite at the cap on purpose, so a full owner can still correct one that is wrong.
    if (!memories.Write(owner, record))
        return Results.Json(new
        {
            error = $"You already have {limits.MaxPerOwner} memories, which is the limit. Forget one "
                  + "that no longer matters, then write this one again.",
        }, statusCode: StatusCodes.Status409Conflict);

    return Results.Ok(MemoryDto.From(record));
});

secured.MapDelete("/memories/{key}", (string key, HttpContext http, IMemoryStore memories) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var owner = MemoryScope.OwnerOf(ConversationSurfaces.Key(http, principal.UserId, chatScope: null));

    // Sanitised the same way the tool sanitises what the model writes, so a key shown in a listing
    // addresses the same row when it comes back. Idempotent: forgetting what is already forgotten is
    // the state the caller asked for, not an error to handle.
    var sanitized = MemoryKey.Sanitize(key);
    if (sanitized is not null)
        memories.Forget(owner, sanitized);

    return Results.NoContent();
});

// The caller's own past chats (the reverse path): list every conversation under their server-derived
// memory namespace web:{userId}, so a fresh browser/device can show history that lives server-side, not
// only in the client. Principal-scoped — a caller can only ever see ITS OWN conversations.
// Each row carries the switches standing on that conversation, so this one call re-states what every
// chat is set to. A surface that showed a remembered value would be reporting its own history back to
// the person: the switches live here, and any other surface may have moved them since.
secured.MapGet("/conversations", (
    HttpContext http, IConversationStore store, IOptions<LlmBackendOptions> llmOptions) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var conversations = store.ListConversations($"{WebSurface}:{principal.UserId}")
        .Select(s => ConversationHistoryMapper.ToSummaryDto(s, principal.UserId, llmOptions.Value.Think))
        .ToArray();
    return Results.Ok(conversations);
});

// One past chat's full transcript (turns + non-destructive compaction checkpoints), oldest-first, so the
// client renders the WHOLE history as it happened. The key is composed exactly as /turn does — the
// server-derived user-id prefix + the sanitised per-chat id — so {id} can only ever address the caller's
// OWN conversation. An unknown id ⇒ an empty transcript (still 200), never another user's data.
secured.MapGet("/conversations/{id}", (
    string id, HttpContext http, IConversationStore store, IPendingConfirmationStore pending,
    IOptions<LlmBackendOptions> llmOptions) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";
    var entries = store.GetHistory(conversationId)
        .Select(ConversationHistoryMapper.ToEntryDto)
        .ToArray();

    // The switches resolved the same way the turn resolves them, so what a client shows is what the
    // next turn will do. Auto-run's floor is false because nothing else is safe to assume of a
    // conversation nobody has armed.
    var preferences = store.GetPreferences(conversationId);

    // What is still awaiting this caller here, restated as the same frame the live path emits. Without
    // it a proposal exists only for the surfaces that were attached when it was staged — so a reload,
    // a second device, or a tap on the notification announcing it all arrive at the assistant saying
    // it staged something, with nothing to approve.
    var waiting = pending.PendingFor(principal.UserId, conversationId)
        .Select((p, i) => TurnFrames.Describe(p.Confirmation, p.Handle, $"cmd_pending_{i}"))
        .ToArray();

    return Results.Ok(new ConversationHistoryDto(
        chatScope ?? string.Empty,
        entries,
        preferences.Think ?? llmOptions.Value.Think,
        preferences.Autorun ?? false,
        waiting));
});

// Soft-delete one of the caller's chats: hides it from their list while keeping the full transcript in the
// append-only history (the self-improvement corpus is never destroyed). The key is composed exactly as the
// reads above — the server-derived user-id prefix + the sanitised per-chat id — so {id} can only ever
// address the caller's OWN conversation. Idempotent; a later turn on the same id (a resume) un-hides it.
secured.MapDelete("/conversations/{id}", (
    string id, HttpContext http, IConversationStore store, IConversationEventBus bus) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = string.IsNullOrEmpty(chatScope)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatScope}";
    store.SoftDelete(conversationId);
    bus.Publish(principal.UserId, new ConversationEvent(
        ConversationStream.Deleted,
        new ConversationChanged(
            chatScope ?? string.Empty,
            http.Request.Headers.TryGetValue(OriginHeaderName, out var named) && named.Count > 0 ? named[0] : null)));
    return Results.NoContent();
});

// How the caller judged one of their OWN answers — the only signal in the corpus that says whether an
// answer was any good, as opposed to how it ran. Deliberately on the user-facing group, not the review
// one: this is satisfaction, written by the person the answer was for, and a reviewer's opinion of
// someone else's conversation is a different fact that the read-only review surface does not collect.
// The key is composed exactly as the reads above, and the store additionally verifies the turn belongs
// to it, so neither the route nor the id can reach another user's turn.
secured.MapPost("/conversations/{id}/turns/{turnId:long}/feedback", (
    string id, long turnId, TurnFeedbackRequest request, HttpContext http, IConversationStore store,
    IConversationEventBus bus) =>
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

    if (!store.SetTurnFeedback(conversationId, turnId, rating, note))
        return Results.NotFound(new { error = "unknown turn." });

    // A verdict is part of what a transcript says, so the caller's other surfaces are told it moved
    // rather than left showing the thumb that stood a moment ago. Sent to every one of their streams
    // and not only the attached ones: a turn id addresses one bubble wherever it is rendered, and a
    // surface reading a different conversation still holds this one in its list.
    bus.Publish(principal.UserId, new ConversationEvent(
        ConversationStream.Feedback,
        new FeedbackChanged(
            chatScope ?? string.Empty,
            http.Request.Headers.TryGetValue(OriginHeaderName, out var named) && named.Count > 0 ? named[0] : null,
            turnId,
            rating switch { TurnFeedbackRating.Up => "up", TurnFeedbackRating.Down => "down", _ => null },
            note)));
    return Results.NoContent();
});

// Compact a conversation on demand: summarise its history in place to free up the context window,
// returning a CompactionOutcome. The key is composed exactly as /turn composes it — the room being
// spoken into, or the server-derived user-id prefix + the sanitised per-chat id — so {id} can only
// ever address the caller's own conversation, and a leaf speaking into a room compacts that room.
// Non-destructive (a checkpoint is appended; the append-only transcript is preserved) and
// idempotent-ish: a conversation with too little history to be worth a model round-trip returns
// Compacted=false, untouched. A model/upstream failure ⇒ 502; the stored history is left as-is.
secured.MapPost("/conversations/{id}/compact", async (
    string id, HttpContext http, IConversationCompactor compactor, IConversationEventBus bus,
    CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var chatScope = ConversationScope.Sanitize(id);
    var conversationId = ConversationSurfaces.Key(http, principal.UserId, chatScope);

    var result = await compactor.CompactAsync(conversationId, ct);
    if (result.IsFailure)
        return Results.Problem(result.Error, statusCode: StatusCodes.Status502BadGateway);

    var outcome = result.Value!;
    // A checkpoint is a transcript change, so the caller's other surfaces re-read it.
    if (outcome.Compacted)
        bus.Publish(principal.UserId, new ConversationEvent(
            ConversationStream.Activity,
            new ConversationChanged(
                chatScope ?? string.Empty,
                http.Request.Headers.TryGetValue(OriginHeaderName, out var named) && named.Count > 0 ? named[0] : null)));
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
// The surface is a query parameter defaulting to web, so every existing caller reads exactly what it
// read before while a reviewer can also ask for the rooms. Refused rather than defaulted when it names
// no surface this service stores: an unrecognised name would otherwise select nothing and report the
// emptiness as a finding about the corpus.
review.MapGet("/conversations/users", (IConversationStore store, string? surface) =>
{
    if (ConversationSurfaces.Resolve(surface ?? WebSurface) is not { } scope)
        return Results.BadRequest(new { error = "unknown surface." });

    var users = store.ListActors(scope)
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
    IOptions<LlmBackendOptions> llm,
    IOptions<LlmAgentOptions> agent,
    IOptions<AssistantServiceOptions> assistant,
    IToolCatalog catalog,
    string? surface) =>
{
    if (ConversationSurfaces.Resolve(surface ?? WebSurface) is not { } scope)
        return Results.BadRequest(new { error = "unknown surface." });

    var stats = store.GetStats(scope);

    // Whether a recorded tool name is one this assistant actually ships is a question only the
    // catalog can answer, and the catalog lives here — the store that counted the calls is
    // domain-blind by design and reports the name it found either way. Asked of the whole catalog
    // rather than the ordinary-turn offer: a conditionally-offered tool (revise_blueprint) is real,
    // and reporting it as invented would send a reviewer chasing a bug that isn't there.
    var tools = stats.Tools
        .Select(t => new AdminToolStatDto(
            t.Name, catalog.CapabilityOf(new Tool(t.Name)) is not null,
            t.Calls, t.MedianMs, t.MaxMs, t.FailedCalls))
        .ToArray();

    return Results.Ok(new AdminConversationStatsDto(
        stats.Conversations, stats.DeletedConversations, stats.Actors, stats.Turns,
        stats.OkTurns, stats.ErrorTurns, stats.CapHitTurns, stats.CancelledTurns, stats.EmptyTurns,
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
            llm.Value.Model, llm.Value.ContextWindow, agent.Value.MaxIterations,
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
review.MapGet("/conversations", (string user, IConversationStore store, string? surface) =>
{
    if (string.IsNullOrWhiteSpace(user))
        return Results.BadRequest(new { error = "user is required." });

    if (ConversationSurfaces.Resolve(surface ?? WebSurface) is not { } scope)
        return Results.BadRequest(new { error = "unknown surface." });

    var conversations = store.ListConversations($"{scope}:{user}", includeDeleted: true)
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
    // Read under whichever surface the handle decodes into, rather than under one chosen here: the
    // listing minted it from a stored key, and that key already says which namespace it belongs to.
    if (!ReviewConversationId.TryDecode(id, ConversationSurfaces.All, out var conversationId, out var scope))
        return Results.NotFound(new { error = "unknown conversation." });

    var userId = ReviewConversationId.UserOf(conversationId, scope);
    var summary = store.ListConversations($"{scope}:{userId}", includeDeleted: true)
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
secured.MapPost("/auth/logout", async (HttpContext http, AuthService auth, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    await auth.LogoutAsync(principal, ct);
    return Results.NoContent();
});

// Ask a turn of a conversation. The turn does NOT run inside this request: it becomes a session with
// its own lifetime, and this request attaches to it. That is what lets a second surface watch the same
// turn, lets any of them stop it, and lets the reply survive the phone that asked for it locking its
// screen. A conversation runs one turn at a time; a second prompt queues behind the running one.
secured.MapPost("/turn", async (
    TurnRequest request,
    HttpContext http,
    IServerAssistant assistant,
    IPendingConfirmationStore pending,
    IOptions<AssistantServiceOptions> assistantOptions,
    AuthService auth,
    IInvocationContext invocation,
    IConversationStore conversations,
    ITurnRegistry turns,
    IConversationEventBus bus,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
        return Results.BadRequest(new { error = "prompt is required." });

    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

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

    // A ROOM instead, when a permitted leaf named one: a conversation belonging to a place, which
    // everyone speaking there continues. The filter has already established that this is the relay
    // path and that the leaf may open rooms — the check cannot be repeated here, because the header
    // itself never reaches this handler.
    var room = ConversationSurfaces.RoomOf(http);
    var conversationId = ConversationSurfaces.Key(http, principal.UserId, chatScope);

    // The leaf this turn arrives through, when one named itself. It picks the prompt overrides the turn
    // is built from and the origin its actions are recorded under; absent, both are the assistant's own.
    var relayLeaf = http.Items.TryGetValue(BearerAuthFilter.RelayLeafKey, out var leafObj) && leafObj is string rl
        ? rl
        : null;

    // The shape this surface needs the reply in. Read from the body on BOTH paths — unlike the
    // conversation id, which is identity-adjacent and therefore only trusted from a relay header, a
    // style says nothing about who is asking or what they may do, so there is nothing here for a
    // header to protect. Unrecognised reads as the full written answer (ReplyStyles.Parse).
    var style = ReplyStyles.Parse(request.Style);

    // Whether this surface wants the answer read aloud as it is written. Read from the body on both
    // paths, like the style and for the same reason: it says nothing about who is asking or what they
    // may do. A host with no speech leaf ignores it — the port reports itself unavailable and no audio
    // frame is ever emitted, so a client that asked simply hears nothing rather than being refused.
    var speak = request.Speak ?? false;

    // How this turn's authority will be established WHEN IT RUNS, which for a queued turn is not now.
    // The relay reads the caller's verified tier off X-Relay-Tier and their auto-accept intent off
    // X-Relay-Auto-Act — a relay host may have no Discord config of its own, so the forwarded tier is
    // the only correct source. A direct session bearer re-derives its own tier from Discord at
    // execution. Either way a caller's capability follows their authority, never the transport.
    //
    // X-Relay-Auto-Act is a FLOOR, not an override: the session ANDs it with the conversation's stored
    // preference, never substitutes it. kgsm-bot pins it false, so a conversation held in Discord can
    // never auto-run whatever is stored against it.
    var authority = http.Items.TryGetValue(BearerAuthFilter.RelayTierKey, out var relayObj) && relayObj is KgsmTier relayTier
        ? new KgsmTierSource(
            FromRelay: true,
            RelayTier: relayTier,
            RelayAutoAct: http.Items.TryGetValue(BearerAuthFilter.RelayAutoActKey, out var autoObj)
                && autoObj is bool b && b)
        : new KgsmTierSource(FromRelay: false, RelayTier: KgsmTier.None, RelayAutoAct: false);

    // Opt into frames with `Accept: text/event-stream`; everyone else gets the buffered JSON contract
    // unchanged. (SSE here is POST, so the SPA reads it via fetch()+ReadableStream — the browser
    // EventSource is GET-only and can't carry the bearer.)
    var wantsStream = http.Request.Headers.Accept
        .Any(v => v is not null && v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));

    // The caller's OTHER surfaces re-read this conversation once the turn has landed: its title, its
    // place in the list and its transcript all just moved. Published even when the turn was cut short,
    // because one abandoned part-way is still a turn that may have been recorded — and the cost of
    // saying so when nothing changed is one re-read.
    //
    // A room publishes nothing: the event names a chat in the caller's own list, and a room is in
    // nobody's list. Sent anyway it would carry the empty chat id and point every one of that person's
    // surfaces at their unpartitioned conversation — a re-read of something that did not change,
    // announcing a turn that happened somewhere else entirely.
    void PublishActivity()
    {
        if (room is not null)
            return;

        bus.Publish(principal.UserId, new ConversationEvent(
            ConversationStream.Activity,
            new ConversationChanged(
                chatScope ?? string.Empty,
                http.Request.Headers.TryGetValue(OriginHeaderName, out var named) && named.Count > 0 ? named[0] : null)));
    }

    if (wantsStream)
    {
        var admission = turns.Admit(
            principal, conversationId, chatScope ?? string.Empty,
            new TurnRun(
                request.Prompt, request.Tools, request.DraftYaml, relayLeaf, authority, room is not null,
                style, speak));

        if (admission.Outcome == TurnAdmission.QueueFull)
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "queue_full",
                        message = "Too many turns are already waiting on this conversation. "
                                + "Wait for one to finish, or cancel one.",
                    },
                },
                statusCode: StatusCodes.Status409Conflict);

        // This response is the session's first consumer. Leaving detaches it and nothing more: the turn
        // belongs to the session, and whether it keeps running is a question about whether its person is
        // still around, not about whether this particular connection survived.
        await SseTurnWriter.AttachAsync(http, admission.Session!, turns);
        PublishActivity();
        return Results.Empty;
    }

    // A BUFFERED caller wants one whole answer, and runs outside the session model: not attachable, not
    // stoppable from another surface, and not queued. That is deliberate rather than an omission —
    // kgsm-bot is the caller, its conversations are keyed by Discord channel where a browser's are keyed
    // by the chat id it minted, so a buffered turn and a watched one are never the same conversation and
    // cannot race for it. Routing a live surface's turn path through the session runner to gain a queue
    // it cannot collide over would be risk bought for nothing.
    //
    // Authority is resolved here for the same reason it is resolved at execution there: it is the moment
    // this turn actually runs.
    var preferences = conversations.GetPreferences(conversationId);
    var think = preferences.Think
        ?? http.RequestServices.GetRequiredService<IOptions<LlmBackendOptions>>().Value.Think;
    var wantsAutoRun = preferences.Autorun ?? false;

    bool canPerform;
    bool autoExecute;
    if (authority.FromRelay)
    {
        canPerform = authority.RelayTier >= KgsmTier.Operator && assistantOptions.Value.ActionsEnabled;
        autoExecute = canPerform && wantsAutoRun && authority.RelayAutoAct;
    }
    else
    {
        canPerform = await auth.CanPerformActionsAsync(principal, ct);
        autoExecute = canPerform && wantsAutoRun && await auth.IsAdminAsync(principal, ct);
    }

    // Attribute any server mutation this turn runs to the asking user, under the surface they were
    // actually using; flows down the awaited turn → tool dispatch → kgsm chokepoint.
    using var provenance = invocation.Begin(
        Invocation.ForAssistant(principal.DisplayName, RelayLeaves.OriginFor(relayLeaf)));

    var result = await assistant.RunAsync(
        conversationId, request.Prompt, canPerform, think, autoExecute, request.Tools, ct,
        request.DraftYaml, principal.DisplayName, relayLeaf, room is not null, style);

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

    PublishActivity();

    var stagedUntil = DateTimeOffset.UtcNow.AddSeconds(
        Math.Max(assistantOptions.Value.Confirmation.TtlSeconds, 1));
    var confirmations = result.Confirmations
        .Select(c => ConfirmationDto.From(c, pending.Put(c, principal.UserId, stagedUntil)))
        .ToArray();

    return Results.Ok(new TurnResponse(result.Text, confirmations, UsageDto.From(result.Usage)));
});

// Stop a running turn, or cancel one that is still waiting. A call rather than a disconnect, because a
// surface that is only watching holds no connection to abort — and every one of that person's surfaces
// can see the turn, so every one of them can end it. Idempotent: two people pressing the same button is
// the ordinary case, not a race to police.
//
// A stop ends THIS turn. What is queued behind it proceeds, exactly as interrupting a command does not
// discard what you typed ahead; discarding one of those is its own call on its own id.
secured.MapDelete("/turns/{turnId}", (string turnId, HttpContext http, ITurnRegistry turns) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    // A turn id is not a handle on somebody else's conversation: an id belonging to another person
    // answers exactly as an unknown one does.
    return turns.Cancel(turnId, principal.UserId)
        ? Results.NoContent()
        : Results.NotFound(new { error = "There is no such turn." });
});

// Point this caller's event stream at a conversation. Turn frames arrive at token rate and mean nothing
// to a surface rendering a different conversation, so they go only to the streams attached to the one
// they belong to; the state events (switches, started, deleted, activity) keep going to every stream,
// because those are about the chat LIST rather than about one conversation.
//
// The stream identifies itself with the id it was given in its `hello` frame — the same header it
// stamps its calls with. What it attached to comes back ON THE STREAM rather than in this response: a
// surface renders from frames, and answering here as well would be a second source for the same state,
// arriving by a different route and able to disagree with it.
secured.MapPost("/events/attach", (
    AttachRequest? request, HttpContext http, IConversationEventBus bus, ITurnRegistry turns) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    if (!http.Request.Headers.TryGetValue(OriginHeaderName, out var named) || named.Count == 0
        || string.IsNullOrWhiteSpace(named[0]))
        return Results.BadRequest(new { error = $"{OriginHeaderName} must name the stream to attach." });

    var streamId = named[0]!;
    var chatId = ConversationScope.Sanitize(request?.ConversationId);
    if (!bus.Attach(streamId, principal.UserId, chatId))
        return Results.NotFound(new { error = "There is no such stream." });

    var conversationId = string.IsNullOrEmpty(chatId)
        ? $"{WebSurface}:{principal.UserId}"
        : $"{WebSurface}:{principal.UserId}:{chatId}";
    var running = turns.Running(conversationId);
    var queued = turns.Queued(conversationId);

    // Either a turn to render, or the fact that there is none — both matter. A surface told nothing
    // cannot tell "nothing is happening" from "the frame has not arrived yet", and would sit on a
    // spinner for a turn that ended before it got here.
    bus.PublishTo(streamId, principal.UserId, running is null
        ? new ConversationEvent(
            ConversationStream.TurnQueue, new TurnQueueEvent(chatId ?? string.Empty, null, queued))
        : new ConversationEvent(ConversationStream.TurnAttach, running.Snapshot(queued)));

    return Results.NoContent();
});

secured.MapPost("/confirm", async (
    ConfirmRequest request,
    HttpContext http,
    IServerAssistant assistant,
    IPendingConfirmationStore pending,
    IPushActionStore pushActions,
    AuthService auth,
    IInvocationContext invocation,
    IOptions<AssistantServiceOptions> assistantOptions,
    CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    // Redeem the handle for THIS caller. Unknown, already redeemed, expired, and staged by a
    // different user all answer the same way — a caller learns that there is nothing to confirm,
    // never which of those it was, so the endpoint is no oracle for handles it was not given.
    if (!pending.TryTake(request.Token, principal.UserId, out var confirmation))
        return Results.BadRequest(new { error = "Invalid or expired confirmation." });

    // Settled here, so the notification's buttons stop being live. They would already fail — the
    // handle they point at is consumed — but a person tapping Confirm on something they confirmed in
    // the chat a moment ago deserves the buttons to be gone rather than a refusal.
    pushActions.VoidForConfirmation(request.Token!);

    // Re-derive authority FRESH at confirm time — never trust it from the token. Mirror the /turn path
    // exactly (the confirm EXECUTES a mutation, so it must read authority the SAME way the propose did):
    // on the trusted-relay path the caller's verified tier arrives as X-Relay-Tier, which is the only
    // correct source for a relay host with no Discord config of its own; a direct session bearer falls
    // back to its own Discord lookup.
    bool canPerform;
    if (http.Items.TryGetValue(BearerAuthFilter.RelayTierKey, out var relayObj) && relayObj is KgsmTier relayTier)
        canPerform = relayTier >= KgsmTier.Operator && assistantOptions.Value.ActionsEnabled;
    else
        canPerform = await auth.CanPerformActionsAsync(principal, ct);
    // The confirming user is the authority for the action they just approved, recorded under the surface
    // they approved it on — a button clicked in Discord is a Discord action.
    var confirmLeaf = http.Items.TryGetValue(BearerAuthFilter.RelayLeafKey, out var confirmLeafObj)
        && confirmLeafObj is string cl ? cl : null;
    using var provenance = invocation.Begin(
        Invocation.ForAssistant(principal.DisplayName, RelayLeaves.OriginFor(confirmLeaf)));

    // A blueprint finalize produces a rich card and, when its repair loop exhausts, a fresh token for the
    // re-edit loop; every other kind produces the outcome verdict. Both shapes are built ONCE here and
    // used by the buffered and streamed paths alike — the two used to build the blueprint response
    // separately, which is two things to keep in step.
    async Task<ConfirmResponse> FinalizeBlueprintAsync(string game, string editedYaml, CancellationToken c)
    {
        var outcome = await assistant.FinalizeBlueprintAsync(game, editedYaml, canPerform, c);
        var data = outcome.Data;

        // On DraftReady, re-stage the returned draft so the user can edit + save again — a fresh
        // handle over a fresh row, exactly as the initial stage was.
        ConfirmationDto[]? reEdit = null;
        if (data is not null && data.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
        {
            var restaged = new PendingConfirmation(
                ConfirmationKind.Blueprint, data.BlueprintName ?? game,
                InstanceName: game, ConfigValue: data.DraftYaml);
            var handle = pending.Put(
                restaged, principal.UserId,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(assistantOptions.Value.Confirmation.TtlSeconds, 1)));
            reEdit = [ConfirmationDto.From(restaged, handle)];
        }

        var card = JsonSerializer.SerializeToElement(ToolResultCard.From(outcome), SseTurnWriter.Json);
        var verified = data?.Outcome == BlueprintAuthoringOutcome.Verified;
        return new ConfirmResponse(outcome.Summary, verified, card, reEdit);
    }

    async Task<ConfirmResponse> ExecuteAsync(PendingConfirmation staged, CancellationToken c)
    {
        var confirmed = await assistant.ConfirmAsync(staged, canPerform, c);
        // Success is the outcome's own — an accepted-but-never-settled lifecycle command is reported as
        // what it is, so a client never has to infer the difference from the text.
        return new ConfirmResponse(
            confirmed.Summary, confirmed.Ok, Outcome: ConfirmOutcomeDto.From(confirmed));
    }

    // Resolve the staged payload before anything runs, so a stale one is still a clean pre-stream 4xx.
    // The staged operation comes back from the store whole — a file body and a blueprint draft are held
    // with it rather than beside it, so there is nothing to rehydrate and nothing that can expire apart
    // from the action it belongs to.
    Func<CancellationToken, Task<ConfirmResponse>> work;
    if (confirmation.Kind == ConfirmationKind.Blueprint)
    {
        // The user reviewed/edited the draft in the chat. The edited YAML rides the request body
        // (re-validated downstream); a save without edits falls back to the draft as staged.
        var game = confirmation.InstanceName ?? confirmation.Target;
        var editedYaml = request.EditedContent;
        if (string.IsNullOrWhiteSpace(editedYaml))
        {
            if (string.IsNullOrWhiteSpace(confirmation.ConfigValue))
                return Results.BadRequest(new { error = "This draft has expired — ask the assistant to draft it again." });
            editedYaml = confirmation.ConfigValue;
        }
        work = c => FinalizeBlueprintAsync(game, editedYaml!, c);
    }
    else
    {
        // write_file carries its complete new content on ConfigValue. An empty one is a staged write
        // with nothing to write — an honest failure, never silently treated as truncating the file.
        if (confirmation.Kind == ConfirmationKind.WriteFile && confirmation.ConfigValue is null)
            return Results.BadRequest(new { error = "This file write has expired or was already confirmed — ask the assistant to propose it again." });

        var staged = confirmation;
        work = c => ExecuteAsync(staged, c);
    }

    // Any confirmation can be slow and silent: a finalize runs a minutes-long pipeline, an install
    // downloads a game, and a lifecycle command is watched until it reaches its run state. Buffered into
    // one response that silence lets an idle-connection reaper on a remote path drop the socket, leaving
    // the caller's card spinning with no terminal result. A caller that opts into
    // `Accept: text/event-stream` gets progress steps + keep-alive heartbeats + a terminal `result` frame
    // carrying the same ConfirmResponse; everyone else (CLI, a plain JSON caller) keeps the buffered
    // contract. Token, authority and payload were all resolved above, so the SSE path only ever commits
    // 200 after a clean validation.
    var wantsStream = http.Request.Headers.Accept
        .Any(v => v is not null && v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
    if (wantsStream)
    {
        await SseConfirmWriter.WriteAsync(
            http, http.RequestServices.GetRequiredService<ITurnProgress>(), work);
        return Results.Empty;
    }

    return Results.Ok(await work(ct));
});

// --- Web Push ----------------------------------------------------------------
// Registering a browser, and the one anonymous route a notification's buttons can reach.

// The application server key a browser subscribes against. Public by definition — it is handed to
// every subscriber and is what a push service verifies this host's tokens with.
secured.MapGet("/push/key", (IPushSubscriptionStore subscriptions, IOptions<AssistantServiceOptions> o) =>
    Results.Ok(new PushKeyResponse(
        subscriptions.Keys().PublicKey,
        o.Value.Push.Enabled)));

// Register this browser. The subscription's keys come from the browser and mean nothing to anyone
// else: they are what the payload is encrypted to, so this host can send to that device and the push
// service routing it cannot read what it carries.
secured.MapPost("/push/subscribe", (
    PushSubscribeRequest request, HttpContext http, IPushSubscriptionStore subscriptions) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    if (string.IsNullOrWhiteSpace(request.Endpoint)
        || string.IsNullOrWhiteSpace(request.P256dh)
        || string.IsNullOrWhiteSpace(request.Auth))
        return Results.BadRequest(new { error = "A subscription needs an endpoint and both keys." });

    if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
        || endpoint.Scheme != Uri.UriSchemeHttps)
        return Results.BadRequest(new { error = "A push endpoint must be an absolute https URL." });

    // The page origin is recorded, never trusted and never branched on: it is here so that a second
    // surface registering against this same leaf needs no schema change, not as a check.
    subscriptions.Register(
        principal.UserId,
        new PushSubscription(request.Endpoint, request.P256dh, request.Auth),
        http.Request.Headers.Origin.FirstOrDefault());

    return Results.NoContent();
});

// Forget this browser, at its owner's request. Scoped to them: an endpoint is not a handle on
// somebody else's device.
// ⚠ [FromBody] is required, not decorative: a DELETE never infers one, and without it the route fails
// to build — which takes the whole endpoint graph with it rather than just this route.
secured.MapDelete("/push/subscribe", (
    [FromBody] PushUnsubscribeRequest request, HttpContext http, IPushSubscriptionStore subscriptions) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    subscriptions.Unregister(principal.UserId, request.Endpoint ?? string.Empty);
    // Idempotent: a browser that unsubscribed locally and then told us is the ordinary sequence, and
    // there being no row to delete is that sequence completing rather than an error.
    return Results.NoContent();
});

// Whether THIS browser is registered, which is the only question its settings screen can ask — it
// knows its own endpoint and nothing about the others.
secured.MapGet("/push/devices", (HttpContext http, IPushSubscriptionStore subscriptions) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var endpoints = subscriptions.For(principal.UserId)
        .Select(d => d.Subscription.Endpoint)
        .ToArray();
    return Results.Ok(new PushDevicesResponse(endpoints));
});

// Redeem a notification's button.
//
// ⚠ ANONYMOUS, and deliberately so: a service worker wakes on an OS push with no page, no bearer and
// nothing it could sign with. The handle is the whole credential — unguessable, single-use, and dead
// with the confirmation it points at. What it is NOT is a way to act without authority: the account
// comes off the handle and that person's tier is resolved HERE, at the tap, exactly as /confirm does.
// A handle minted while somebody was an operator does not keep them one.
app.MapPost("/push/actions/{handle}", async (
    string handle,
    IPushActionStore pushActions,
    IPendingConfirmationStore pending,
    PushConfirmationRunner runner,
    AuthService auth,
    CancellationToken ct) =>
{
    if (!pushActions.TryTake(handle, out var action))
        return Results.Ok(new PushActionResponse(false, "That notification is no longer valid."));

    if (action.Verb == PushActionVerb.Cancel)
    {
        // Cancelling is taking the handle and doing nothing with it: the staged operation is consumed
        // and can never run. It needs no authority — declining to act is not an action.
        pending.TryTake(action.ConfirmationHandle, action.Stager.UserId, out _);
        return Results.Ok(new PushActionResponse(true, "Cancelled."));
    }

    if (!pending.TryTake(action.ConfirmationHandle, action.Stager.UserId, out var confirmation))
        return Results.Ok(new PushActionResponse(false, "That action has already been handled or has expired."));

    // The identity was recorded when the action was staged; what is derived HERE is the authority, off
    // the live account store. So the session this tap does not have is the only thing missing, and the
    // check is otherwise the one /confirm makes: somebody demoted since staging is refused now.
    var principal = new AuthPrincipal(
        action.Stager.Provider, action.Stager.UserId, action.Stager.DisplayName, SessionId: string.Empty);

    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    if (!canPerform)
        return Results.Ok(new PushActionResponse(false, "You are no longer allowed to run that action."));

    // ⚠ Started, NOT awaited. A confirmed action runs to completion — a backup is minutes and the
    // executor allows fifteen — and the caller is a service worker the browser will terminate long
    // before that. Holding the request open means the tap appears to do nothing at all while the work
    // runs. The verdict comes back as a second push, to the device that approved it.
    runner.Start(confirmation, action, canPerform);

    // What is claimed here is only what is known here: it was approved and it has been started.
    var verb = ConfirmationKinds.Verb(confirmation.Kind);
    return Results.Ok(new PushActionResponse(
        true, $"Confirmed — running the {verb} now. I'll let you know how it goes."));
});

app.Run();

/// <summary>Exposed so the test project's <c>WebApplicationFactory</c> can boot the app.</summary>
public partial class Program;
