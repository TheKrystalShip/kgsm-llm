using System.Text.Json;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Service;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Discord;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Ollama;

var builder = WebApplication.CreateBuilder(args);

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

// --- Security ----------------------------------------------------------------
builder.Services.AddSingleton<ConfirmationTokenService>();

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
            "(ClientSecret/BotToken/GuildId/ActionRoleId) — no caller will be authorized for actions.");

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
            logger.LogInformation("kgsm event received: {EventType}", eventType.GetString());
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
secured.MapGet("/tools", async (HttpContext http, DiscordAuthService auth, IPromptOverrides promptOverrides, CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);

    var tools = canPerform ? LlmTools.All : LlmTools.ReadOnly;
    tools = promptOverrides.OverlayTools(tools);

    var dtos = tools.Select(t => new ToolDto(
        t.Name,
        t.Description,
        t.Parameters.Select(p => new ToolParameterDto(
            p.Name, p.Description, p.Required, p.Type, p.AllowedValues)).ToArray())).ToArray();

    return Results.Ok(dtos);
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
    DiscordAuthService auth,
    IInvocationContext invocation,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
        return Results.BadRequest(new { error = "prompt is required." });

    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    // Authority is derived fresh from the verified principal; the conversation key is
    // principal-scoped so one user can't read or poison another's memory.
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    var think = request.Think
        ?? http.RequestServices.GetRequiredService<IOptions<OllamaOptions>>().Value.Think;
    var conversationId = $"web:{principal.UserId}";

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
            http, assistant, tokens, principal, conversationId, request.Prompt, canPerform, think, request.Tools);
        return Results.Empty;
    }

    var result = await assistant.RunAsync(conversationId, request.Prompt, canPerform, think, request.Tools, ct);

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
        .Select(c => new ConfirmationDto(
            c.Kind.ToString().ToLowerInvariant(), c.Target, c.InstanceName, tokens.Create(c, principal.UserId),
            c.ConfigKey, c.ConfigValue))
        .ToArray();

    return Results.Ok(new TurnResponse(result.Text, confirmations, UsageDto.From(result.Usage)));
});

secured.MapPost("/confirm", async (
    ConfirmRequest request,
    HttpContext http,
    IServerAssistant assistant,
    ConfirmationTokenService tokens,
    DiscordAuthService auth,
    IInvocationContext invocation,
    CancellationToken ct) =>
{
    var principal = (AuthPrincipal)http.Items[BearerAuthFilter.PrincipalKey]!;

    // Reject a malformed/expired token AND a token staged by a different user — with the same
    // generic message, so it isn't an oracle for which case occurred.
    if (!tokens.TryValidate(request.Token, out var confirmation, out var stagedBy) ||
        !string.Equals(stagedBy, principal.UserId, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid or expired confirmation." });

    // Re-derive authority FRESH at confirm time — never trust it from the token.
    var canPerform = await auth.CanPerformActionsAsync(principal, ct);
    // The confirming user is the authority for the action they just approved (origin=assistant).
    using var provenance = invocation.Begin(Invocation.ForAssistant(principal.DisplayName));
    var result = await assistant.ConfirmAsync(confirmation, canPerform, ct);

    return Results.Ok(new ConfirmResponse(
        result.IsSuccess ? result.Value! : result.Error!, result.IsSuccess));
});

app.Run();

/// <summary>Exposed so the test project's <c>WebApplicationFactory</c> can boot the app.</summary>
public partial class Program;
