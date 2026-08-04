using System.Text.Json;

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
using TheKrystalShip.Kgsm.Assistant.Service.Discord;
using TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;
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
// The SPA is a separate origin (GitHub Pages), so auth is a bearer session token the
// service mints — not a cookie. The three in-memory stores are SINGLETONS; the typed
// HttpClient is transient (factory-managed); the orchestration service + bearer filter
// are SCOPED — so no singleton ever captures the transient client.
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<OAuthStateStore>();
builder.Services.AddSingleton<RoleCache>();
builder.Services.AddHttpClient<IDiscordOAuthClient, DiscordOAuthClient>(
    c => c.BaseAddress = new Uri("https://discord.com/"));
builder.Services.AddScoped<DiscordAuthService>();
builder.Services.AddScoped<BearerAuthFilter>();

// CORS: allow the configured SPA origin to call with an Authorization header. NO
// AllowCredentials (bearer, not cookies). UseCors is ordered before the secured group so
// cross-origin preflight (OPTIONS) is answered by the CORS middleware, pre-auth.
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
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
    var discord = app.Services.GetRequiredService<IOptions<DiscordOAuthOptions>>().Value;
    if (opts.ActionsEnabled && !tokens.IsConfigured)
        app.Logger.LogWarning(
            "Assistant:ActionsEnabled is true but Assistant:Confirmation:Key is unset — " +
            "the service will run READ-ONLY until a key is configured.");
    if (opts.ActionsEnabled &&
        (string.IsNullOrEmpty(discord.ClientSecret) || string.IsNullOrEmpty(discord.BotToken) ||
         string.IsNullOrEmpty(discord.GuildId) || string.IsNullOrEmpty(discord.ActionRoleId)))
        app.Logger.LogWarning(
            "Assistant:ActionsEnabled is true but DiscordOAuth is not fully configured " +
            "(ClientSecret/BotToken/GuildId/ActionRoleId) — direct SESSION-bearer callers can't be " +
            "authorized for actions. The trusted relay (kgsm-api) path is unaffected: it uses the " +
            "api's verified tier (X-Relay-Can-Act), not a Discord lookup.");

    // The bot token now resolves guild membership AND roles (the caller's OAuth token is
    // discarded after /users/@me), so without it no login can succeed at all.
    if (string.IsNullOrEmpty(discord.BotToken) && !string.IsNullOrEmpty(discord.GuildId))
        app.Logger.LogWarning(
            "DiscordOAuth:BotToken is unset — guild-membership and role lookups use the bot " +
            "token, so every login will be denied until it is configured.");
}

// --- Public endpoints --------------------------------------------------------
// Open: a liveness probe, and the two auth-bootstrap endpoints (a caller has no session yet).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Returns the Discord authorize URL (with a fresh single-use state + PKCE challenge) for the
// SPA to navigate the browser to. JSON, not a 302 — the SPA owns navigation.
app.MapGet("/auth/login", (DiscordAuthService auth) =>
    Results.Ok(new LoginUrlResponse(auth.BuildLoginUrl())));

// The SPA POSTs the code + state Discord handed back. The service exchanges it server-side,
// requires guild membership, and returns a session bearer token. 401 if anything fails.
app.MapPost("/auth/callback", async (AuthCallbackRequest request, DiscordAuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
        return Results.BadRequest(new { error = "code and state are required." });

    var session = await auth.CompleteLoginAsync(request.Code, request.State, ct);
    return session is null
        ? Results.Unauthorized()
        : Results.Ok(new AuthSessionResponse(session.SessionToken, session.DisplayName));
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
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    return Results.Ok(new MeResponse(principal.UserId, principal.DisplayName, canPerform));
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
    var conversations = store.ListConversations($"web:{principal.UserId}")
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
        ? $"web:{principal.UserId}"
        : $"web:{principal.UserId}:{chatScope}";
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
        ? $"web:{principal.UserId}"
        : $"web:{principal.UserId}:{chatScope}";
    store.SoftDelete(conversationId);
    return Results.NoContent();
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
        ? $"web:{principal.UserId}"
        : $"web:{principal.UserId}:{chatScope}";

    var result = await compactor.CompactAsync(conversationId, ct);
    if (result.IsFailure)
        return Results.Problem(result.Error, statusCode: StatusCodes.Status502BadGateway);

    var outcome = result.Value!;
    return Results.Ok(new CompactionResultDto(outcome.Compacted, outcome.MessagesCompacted, outcome.Summary));
});

secured.MapPost("/auth/logout", (HttpContext http, DiscordAuthService auth) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    auth.Logout(principal);
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

    // Whether THIS turn may perform actions = the user's per-turn toggle (INTENT) ∧ their AUTHORITY.
    //  - trusted relay (kgsm-api): authority is the api's verified tier decision (operator+), already
    //    folded with the toggle into X-Relay-Can-Act, which BearerAuthFilter stashed. We add only our
    //    local preconditions (ActionsEnabled + a Confirmation signing key to mint the proposal token).
    //  - direct session bearer: authority is the caller's Discord action role, ANDed with the toggle.
    // The conversation key is principal-scoped so one user can't read or poison another's memory.
    // autoExecute = auto-accept: on a trusted-relay turn the api ALSO forwards an admin-tier ∧ toggle
    // decision (X-Relay-Auto-Act). When set, the dispatcher RUNS lifecycle commands immediately
    // instead of staging them. It is gated to canPerform so the propose-gate (BuildGate) always
    // allows what auto-execute then runs; the direct-bearer path never auto-executes (propose-only).
    bool canPerform;
    bool autoExecute = false;
    if (http.Items.TryGetValue(BearerAuthFilter.RelayCanActKey, out var relayObj) && relayObj is bool relayCanAct)
    {
        var asstOpts = http.RequestServices.GetRequiredService<IOptions<AssistantServiceOptions>>().Value;
        canPerform = relayCanAct && asstOpts.ActionsEnabled && tokens.IsConfigured;
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
        ? $"web:{principal.UserId}"
        : $"web:{principal.UserId}:{chatScope}";

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

    var result = await assistant.RunAsync(conversationId, request.Prompt, canPerform, think, autoExecute, request.Tools, ct, request.DraftYaml);

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

    // Re-derive authority FRESH at confirm time — never trust it from the token. Mirror the /turn
    // path exactly (the confirm executes a mutation, so it must read authority the SAME way the propose
    // did): on the trusted-relay path the api's verified operator-tier decision arrives as X-Relay-Can-Act
    // (BearerAuthFilter stashed it) — a Discord-less relay host has no bot to re-derive from, so honoring
    // the header is the only correct source; a direct session bearer falls back to the Discord action role.
    bool canPerform;
    if (http.Items.TryGetValue(BearerAuthFilter.RelayCanActKey, out var relayObj) && relayObj is bool relayCanAct)
        canPerform = relayCanAct && assistantOptions.Value.ActionsEnabled && tokens.IsConfigured;
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
