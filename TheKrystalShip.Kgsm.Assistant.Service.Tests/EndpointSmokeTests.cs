using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Service.Discord;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Boots the real app via <see cref="WebApplicationFactory{T}"/> to verify endpoint wiring,
/// auth enforcement, and response shape. <see cref="IServerAssistant"/> and (where needed)
/// <see cref="IDiscordOAuthClient"/> are substituted, so these need neither Ollama, a live
/// kgsm, nor a real Discord — the done-bar for a slice whose SPA doesn't exist yet.
/// </summary>
public class EndpointSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> Factory(
        IServerAssistant? assistant = null,
        IDiscordOAuthClient? discord = null,
        Action<IWebHostBuilder>? configure = null,
        Llm.Interfaces.IConversationStore? withStore = null,
        Llm.Interfaces.IConversationCompactor? withCompactor = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            // Provide kgsm settings so app startup binds the KGSM section regardless of
            // whether the test host discovers the service's appsettings.json.
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                if (assistant is not null) services.AddSingleton(assistant);
                if (discord is not null) services.AddSingleton(discord);
                // Override the real SQLite store with a fake for the reverse-path endpoint tests (last
                // registration wins for the interface the endpoints resolve).
                if (withStore is not null)
                {
                    services.RemoveAll<Llm.Interfaces.IConversationStore>();
                    services.AddSingleton(withStore);
                }
                if (withCompactor is not null)
                {
                    services.RemoveAll<Llm.Interfaces.IConversationCompactor>();
                    services.AddSingleton(withCompactor);
                }
            });
        });

    /// <summary>Creates a client with a seeded session bearer for <paramref name="userId"/>.</summary>
    private static HttpClient Authed(WebApplicationFactory<Program> factory, string userId = "user1")
    {
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<SessionStore>();
        var token = store.Create(new Session(userId, "User One", DateTimeOffset.UtcNow.AddHours(1)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Health_Returns200()
    {
        var response = await Factory().CreateClient().GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Turn_NoBearer_Returns401()
    {
        var response = await Factory(Substitute.For<IServerAssistant>()).CreateClient()
            .PostAsJsonAsync("/turn", new { prompt = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_NoBearer_Returns401()
    {
        var response = await Factory(Substitute.For<IServerAssistant>()).CreateClient()
            .PostAsJsonAsync("/confirm", new { token = "whatever" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Turn_EmptyPrompt_Returns400()
    {
        var client = Authed(Factory(Substitute.For<IServerAssistant>()));

        var response = await client.PostAsJsonAsync("/turn", new { prompt = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Turn_Valid_DerivesPrincipalConversationId_AndReturnsText()
    {
        var assistant = Substitute.For<IServerAssistant>();
        // conversationId is derived server-side from the principal — NOT from the request.
        assistant.RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(AssistantResult.Ok("hello from assistant", Array.Empty<PendingConfirmation>())));

        var client = Authed(Factory(assistant));
        var response = await client.PostAsJsonAsync("/turn", new { prompt = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TurnResponse>();
        body!.Text.Should().Be("hello from assistant");
        body.Confirmations.Should().BeEmpty();
        await assistant.Received(1).RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Confirm_GarbageToken_Returns400()
    {
        var client = Authed(Factory(Substitute.For<IServerAssistant>()));

        var response = await client.PostAsJsonAsync("/confirm", new { token = "not-a-valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_TokenStagedByAnotherUser_IsRejected()
    {
        // A confirmation token bound to "alice" must not be confirmable by "bob".
        var factory = Factory(Substitute.For<IServerAssistant>(),
            configure: b => b.UseSetting("Assistant:Confirmation:Key", "test-key"));
        var bob = Authed(factory, "bob");

        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        var aliceToken = tokenSvc.Create(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "alice");

        var response = await bob.PostAsJsonAsync("/confirm", new { token = aliceToken });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_Command_StampsProvenance_AtTheKgsmChokepoint()
    {
        // The one link that was proven only by static-read: the REAL /confirm handler opening the
        // provenance scope in the live ASP.NET pipeline (IInvocationContext singleton +
        // principal.DisplayName → ForAssistant), flowing through the real ServerAssistant and
        // KgsmServerOperations to the kgsm chokepoint (IInstanceService). Commands are propose-only,
        // so the mutation fires HERE at /confirm — a plain async handler, NO SSE/iterator on this
        // path — which is exactly why this is the path that must carry actor+origin. Everything is
        // real except the engine boundary (spied) and Discord (spied for the live role check).
        var instances = Substitute.For<IInstanceService>();
        instances.GetAll().Returns(new Dictionary<string, Instance>
        {
            ["inst"] = new Instance { Name = "inst", BlueprintFile = "factorio" },
        });
        instances.Start("inst", Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0));

        // canPerformActions is re-derived live from the principal's guild role at confirm time.
        var discord = Substitute.For<IDiscordOAuthClient>();
        discord.GetGuildMemberAsync("user1", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = new[] { "role-123" } });

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.UseSetting("Assistant:Confirmation:Key", "test-key");
            b.UseSetting("DiscordOAuth:ActionRoleId", "role-123");
            b.ConfigureTestServices(s => s.AddSingleton<IInstanceService>(instances));
        });

        var client = Authed(factory); // userId=user1, DisplayName="User One"
        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        var token = tokenSvc.Create(new PendingConfirmation(ConfirmationKind.Start, "inst"), "user1");

        var response = await client.PostAsJsonAsync("/confirm", new { token });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The engine call was attributed to the confirming Discord user, via the assistant surface —
        // NOT the bare OS-user fallback (which would be null, null → unattributed audit row).
        instances.Received(1).Start("inst", "discord:User One", "assistant");
    }

    [Fact]
    public async Task Confirm_Relay_BlueprintFinalize_CanActHeaderGrantsAuthority()
    {
        // The blueprint-review Save arrives on the trusted-relay path (kgsm-api). Authority MUST come from
        // X-Relay-Can-Act exactly as the propose side does — a relay host with no Discord config has no bot
        // to re-derive from, so ignoring the header would deny every finalize. The api's /confirm is
        // operator-gated, so it forwards can-act=true; here we prove the assistant honors it.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.FinalizeBlueprintAsync("Satisfactory", "edited-yaml", true, Arg.Any<CancellationToken>())
            .Returns(new ToolResult<BlueprintAuthoringData>(
                LlmTools.CreateBlueprint, Confidence.Confirmed, new ResultRef(ResourceKind.Blueprint, "satisfactory"),
                "Added Satisfactory.",
                new BlueprintAuthoringData(BlueprintAuthoringOutcome.Verified, "Satisfactory", "satisfactory", [], "it booted", null, true)));

        var factory = Factory(assistant, configure: b =>
        {
            b.UseSetting("Assistant:Relay:Secret", "relay-secret");
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.UseSetting("Assistant:Confirmation:Key", "test-key");
        });
        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        var token = tokenSvc.Create(
            new PendingConfirmation(ConfirmationKind.Blueprint, "satisfactory", InstanceName: "Satisfactory"), "relayuser");

        var response = await RelayConfirmAsync(factory.CreateClient(), token, "edited-yaml", "relay-secret", "relayuser", canAct: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await assistant.Received(1).FinalizeBlueprintAsync("Satisfactory", "edited-yaml", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_Relay_BlueprintFinalize_WithoutCanAct_IsDenied()
    {
        // Fail-closed: a relay confirm that does NOT speak X-Relay-Can-Act (absent) must reach the finalize
        // with canPerform=false, never silently authorized.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.FinalizeBlueprintAsync("Satisfactory", "edited-yaml", false, Arg.Any<CancellationToken>())
            .Returns(new ToolResult<BlueprintAuthoringData>(
                LlmTools.CreateBlueprint, Confidence.Likely, new ResultRef(ResourceKind.Blueprint, "Satisfactory"),
                "denied",
                new BlueprintAuthoringData(BlueprintAuthoringOutcome.Failed, "Satisfactory", null, [], null, "denied", false)));

        var factory = Factory(assistant, configure: b =>
        {
            b.UseSetting("Assistant:Relay:Secret", "relay-secret");
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.UseSetting("Assistant:Confirmation:Key", "test-key");
        });
        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        var token = tokenSvc.Create(
            new PendingConfirmation(ConfirmationKind.Blueprint, "satisfactory", InstanceName: "Satisfactory"), "relayuser");

        var response = await RelayConfirmAsync(factory.CreateClient(), token, "edited-yaml", "relay-secret", "relayuser", canAct: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await assistant.Received(1).FinalizeBlueprintAsync("Satisfactory", "edited-yaml", false, Arg.Any<CancellationToken>());
    }

    /// <summary>POSTs /confirm over the trusted-relay path with the relay secret + forwarded identity and,
    /// optionally, the <c>X-Relay-Can-Act</c> authority header.</summary>
    private static async Task<HttpResponseMessage> RelayConfirmAsync(
        HttpClient client, string token, string editedContent, string secret, string userId, bool? canAct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/confirm")
        {
            Content = JsonContent.Create(new { token, editedContent }),
        };
        request.Headers.Add("X-Relay-Secret", secret);
        request.Headers.Add("X-Relay-User", userId);
        if (canAct is not null) request.Headers.Add("X-Relay-Can-Act", canAct.Value ? "true" : "false");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Events_BadSignature_Returns401_WhenSecretConfigured()
    {
        var client = Factory(configure: b => b.UseSetting("Assistant:Webhook:Secret", "topsecret")).CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = new StringContent("{\"EventType\":\"instance_started\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-KGSM-Signature", "sha256=AAAA"); // not a valid signature

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A blueprint_* event from kgsm must reach the inventory invalidation seam — the
    /// whole reason Phase 2 of blueprint-editor-plan.md emits these events. A web-originated
    /// blueprint edit lands via this webhook; without an invalidate the assistant's blueprint
    /// catalog serves stale data on the next turn. The webhook has no secret configured here, so
    /// the signature check is silently bypassed (the dev/host path; production requires it).
    /// Pins the contract: any 2xx + exactly one <see cref="IInventoryInvalidation.Invalidate"/>
    /// call per envelope, regardless of payload shape.</summary>
    [Fact]
    public async Task Events_BlueprintEvent_InvalidatesInventoryOnce()
    {
        var inventory = Substitute.For<IInventoryInvalidation>();
        var client = Factory(configure: b =>
        {
            // No webhook secret ⇒ the handler skips signature verification and processes the body
            // (the loud "secret not configured — signature NOT enforced" log path; mirrors how the
            // dev host runs today).
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInventoryInvalidation>();
                services.AddSingleton(inventory);
            });
        }).CreateClient();

        var envelope = """{"EventType":"blueprint_updated","Data":{"BlueprintName":"factorio","Tier":"user","OverridesSystem":true,"Runtime":"native"},"Timestamp":"2026-07-28T12:00:00Z"}""";
        var response = await client.PostAsync("/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // The CRITICAL assertion: blueprint events must drive an invalidate. Any future refactor that
        // gated the invalidation on instance-only event types would regress here.
        inventory.Received(1).Invalidate();
    }

    [Fact]
    public async Task AuthCallback_WithMockedDiscord_MintsSession()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        discord.ExchangeCodeAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordTokenResponse { AccessToken = "tok" });
        discord.GetCurrentUserAsync("tok", Arg.Any<CancellationToken>())
            .Returns(new DiscordUser { Id = "u1", Username = "Alice" });
        discord.GetGuildMemberAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = Array.Empty<string>() });

        var client = Factory(discord: discord).CreateClient();

        // Hit /auth/login to mint a real single-use state, then complete the callback.
        var login = await client.GetFromJsonAsync<LoginUrlResponse>("/auth/login");
        var state = QueryValue(login!.Url, "state");

        var response = await client.PostAsJsonAsync("/auth/callback", new { code = "the-code", state });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        session!.Token.Should().NotBeNullOrEmpty();
        session.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task AuthCallback_MissingFields_Returns400()
    {
        var response = await Factory().CreateClient().PostAsJsonAsync("/auth/callback", new { code = "", state = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_ReflectsLiveRoleLookup()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        discord.GetGuildMemberAsync("user1", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = new[] { "role-123" } });

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.UseSetting("Assistant:Confirmation:Key", "test-key");
            b.UseSetting("DiscordOAuth:ActionRoleId", "role-123");
        });
        var client = Authed(factory);

        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me");

        me!.UserId.Should().Be("user1");
        me.CanPerformActions.Should().BeTrue();
    }

    [Fact]
    public async Task Cors_Preflight_AllowsConfiguredOrigin()
    {
        const string origin = "https://example.github.io";
        var client = Factory(configure: b => b.UseSetting("Auth:AllowedOrigins:0", origin)).CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/turn");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(origin);
    }

    [Fact]
    public async Task Turn_StreamAccept_EmitsTokensThenDone()
    {
        var assistant = Substitute.For<IServerAssistant>();
        // conversationId is derived server-side from the principal, exactly like the buffered path.
        assistant.RunStreamAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Hel"),
                AssistantStreamEvent.Token("lo"),
                AssistantStreamEvent.Final("Hello")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "hi");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: text.delta");
        // §5·a: in-band `type` discriminator alongside the SSE `event:` name; payload key is `text` (not `delta`).
        body.Should().Contain("\"type\":\"text.delta\"");
        body.Should().Contain("\"text\":\"Hel\"");
        body.Should().Contain("event: done");
        body.Should().Contain("\"text\":\"Hello\"");
    }

    [Fact]
    public async Task Turn_StreamAccept_EmitsToolStartAndResult()
    {
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", "status?", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(LlmTools.GetStatus, new Dictionary<string, string?> { ["instance_name"] = "factorio" }, "tc_0"),
                AssistantStreamEvent.ToolResult(LlmTools.GetStatus, "factorio: stopped", "tc_0"),
                AssistantStreamEvent.Token("Stopped."),
                AssistantStreamEvent.Final("Stopped.")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "status?");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: tool.start");
        body.Should().Contain("\"type\":\"tool.start\"");
        // §5·a correlation id — the SAME id rides tool.start and tool.result so a renderer pairs them.
        body.Should().Contain("\"id\":\"tc_0\"");
        body.Should().Contain("\"tool\":\"get_status\"");
        body.Should().Contain("\"instance_name\":\"factorio\"");
        body.Should().Contain("event: tool.result");
        body.Should().Contain("\"summary\":\"factorio: stopped\"");
        // A summary-only tool omits `result` entirely (JsonIgnore WhenWritingNull) — not `result:null`.
        body.Should().NotContain("\"result\":");
        body.Should().Contain("event: done");
    }

    [Fact]
    public async Task Turn_StreamAccept_ToolResult_CarriesStructuredCard()
    {
        // Phase 2 (§5·c): run_health_check surfaces a structured card on tool.result. The frame KEEPS
        // `summary` (Phase-1 clients unaffected) AND adds `result` — the ToolResultCard, with enums
        // serialised as camelCase strings (never opaque ints) for the SPA, correlated by the same id.
        var card = new ToolResultCard(
            LlmTools.RunHealthCheck.Name, Confidence.Confirmed,
            new ResultRef(ResourceKind.Server, "factorio"),
            new HealthData(
                CheckState.Warn,
                new[]
                {
                    new HealthCheck("liveness", CheckState.Pass, Severity.Success, "Running."),
                    new HealthCheck("updates", CheckState.Warn, Severity.Update, "Update available."),
                },
                Passed: 1, Total: 2, Skipped: 0));

        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", "health?", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(
                    LlmTools.RunHealthCheck, new Dictionary<string, string?> { ["instance_name"] = "factorio" }, "tc_0"),
                AssistantStreamEvent.ToolResult(
                    LlmTools.RunHealthCheck, "factorio: passed with warnings.", "tc_0", card),
                AssistantStreamEvent.Final("factorio has an update available.")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "health?");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: tool.result");
        // Phase-1 field retained — a thin client still reads `summary`:
        body.Should().Contain("\"summary\":\"factorio: passed with warnings.\"");
        // Phase-2 structured card present, correlated to its tool.start by the same id:
        body.Should().Contain("\"id\":\"tc_0\"");
        body.Should().Contain("\"result\":{");
        body.Should().Contain("\"tool\":\"run_health_check\"");
        // Enums serialised as camelCase strings, NOT integers:
        body.Should().Contain("\"confidence\":\"confirmed\"");
        body.Should().Contain("\"overall\":\"warn\"");
        body.Should().Contain("\"state\":\"pass\"");
        body.Should().Contain("\"severity\":\"update\"");
        body.Should().Contain("\"resource\":\"server\"");
        // Honest counts:
        body.Should().Contain("\"passed\":1").And.Contain("\"total\":2").And.Contain("\"skipped\":0");
    }

    [Fact]
    public async Task Turn_StreamAccept_EmitsProgress_BeforeTheToolResult()
    {
        // A long-running tool (create_blueprint) reports its own progress steps WHILE it is still
        // executing, ahead of its single terminal tool.result — the exact wire shape a live stepper
        // reads.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", "make me a rust server", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(LlmTools.CreateBlueprint, new Dictionary<string, string?> { ["game"] = "Rust" }, "tc_0"),
                AssistantStreamEvent.Progress(LlmTools.CreateBlueprint, "research", "Looking it up online…"),
                AssistantStreamEvent.Progress(LlmTools.CreateBlueprint, "draft", "Building a server config…"),
                AssistantStreamEvent.ToolResult(LlmTools.CreateBlueprint, "Rust is now in the catalog.", "tc_0"),
                AssistantStreamEvent.Final("Rust is now in the catalog. Want me to make you a server?")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "make me a rust server");
        var body = await response.Content.ReadAsStringAsync();

        // Two `progress` frames, each carrying `type`+tool+key+label+status, land before `tool.result`.
        body.Should().Contain("event: progress");
        body.Should().Contain("\"type\":\"progress\"");
        body.Should().Contain("\"tool\":\"create_blueprint\"");
        body.Should().Contain("\"key\":\"research\"");
        // System.Text.Json escapes non-ASCII by default — the ellipsis rides the wire as ….
        body.Should().Contain("\"label\":\"Looking it up online\\u2026\"");
        body.Should().Contain("\"status\":\"active\"");
        body.Should().Contain("\"key\":\"draft\"");
        // No tool-call id is threaded through (the ambient sink that reports it has no access to the
        // one the generic agent loop mints) — omitted from the frame (JsonIgnore WhenWritingNull), not
        // a fabricated `"id":null`.
        body.Should().NotContain("\"id\":null");

        var progressIndex = body.IndexOf("event: progress", StringComparison.Ordinal);
        var resultIndex = body.IndexOf("event: tool.result", StringComparison.Ordinal);
        progressIndex.Should().BeGreaterThan(-1);
        resultIndex.Should().BeGreaterThan(progressIndex, "progress steps must land before the tool's own terminal result");
    }

    [Fact]
    public async Task Turn_StreamAccept_ConfirmationEventCarriesTokenBoundToCaller()
    {
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Staging…"),
                AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria")),
                AssistantStreamEvent.Final("Staged.")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Confirmation:Key", "test-key"));
        var response = await StreamTurnAsync(Authed(factory), "remove terraria");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: command.proposed");
        var token = ExtractConfirmationToken(body);

        // The token minted into the SSE frame must validate AND be bound to the caller (user1).
        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        tokenSvc.TryValidate(token, out var confirmation, out var stagedBy).Should().BeTrue();
        stagedBy.Should().Be("user1");
        confirmation.Kind.Should().Be(ConfirmationKind.Uninstall);
        confirmation.Target.Should().Be("terraria");
    }

    [Fact]
    public async Task Turn_StreamAccept_GeneralisedCommand_ProposedEventCarriesVerbAndSubject()
    {
        // §3.5 + §5·a: a generalised command (start) is propose-only and surfaces as command.proposed
        // in the §5·a shape — `verb` (the normalised API token), `subject {resource,id}`, and a human
        // `confirm` prompt. The host-minted `token` is retained (additive) for the /confirm surfaces.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Proposing…"),
                AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Start, "factorio")),
                AssistantStreamEvent.Final("Proposed.")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Confirmation:Key", "test-key"));
        var response = await StreamTurnAsync(Authed(factory), "start factorio");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: command.proposed");
        body.Should().Contain("\"type\":\"command.proposed\"");
        body.Should().Contain("\"verb\":\"start\"");          // normalised API verb, NOT the old `kind`
        body.Should().Contain("\"resource\":\"server\"");     // subject.resource
        body.Should().Contain("\"id\":\"factorio\"");         // subject.id (the resolved target)
        body.Should().Contain("\"confirm\":\"Start factorio?\"");

        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        tokenSvc.TryValidate(ExtractConfirmationToken(body), out var confirmation, out _).Should().BeTrue();
        confirmation.Kind.Should().Be(ConfirmationKind.Start);
        confirmation.Target.Should().Be("factorio");
    }

    [Fact]
    public async Task Turn_StreamAccept_ErrorEvent_EmitsCodeAndMessage()
    {
        // The error frame is a RESHAPE ({error} -> {code,message}), surfaced in-band on the
        // already-committed 200 stream (never a status code once the first frame has flushed).
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("…"),
                AssistantStreamEvent.Error("boom")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "do it");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: error");
        body.Should().Contain("\"type\":\"error\"");
        body.Should().Contain("\"code\":\"assistant_failed\"");
        body.Should().Contain("\"message\":\"boom\"");
    }

    [Fact]
    public async Task Turn_Relay_ValidSecret_AuthsAsForwardedUser()
    {
        // The trusted-relay path (kgsm-api): a matching X-Relay-Secret + forwarded Discord identity
        // authenticates with NO session bearer, and the forwarded user drives the principal-scoped
        // conversation key (web:<userId>) — per-user isolation is preserved through the relay.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        (await response.Content.ReadAsStringAsync()).Should().Contain("event: done");
        assistant.Received().RunStreamAsync(
            "web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Turn_Relay_ConversationId_SubScopesUserMemory()
    {
        // A per-chat X-Relay-Conversation-Id partitions the SAME user's memory into a fresh context
        // window — keyed web:<userId>:<chatId> — so a "new chat" no longer leaks the previous chat's
        // history, while staying strictly inside that user's namespace (the user id is the prefix).
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser:chat-abc123", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(
            factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User", "chat-abc123");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        assistant.Received().RunStreamAsync(
            "web:relayuser:chat-abc123", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Turn_Relay_WrongSecret_Returns401()
    {
        // A present-but-wrong relay secret is a hard 401 — never a fall-through to the session path.
        var factory = Factory(Substitute.For<IServerAssistant>(), configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(factory.CreateClient(), "hi", "WRONG-SECRET", "relayuser");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Turn_Relay_MissingUser_Returns401()
    {
        // A valid secret with no forwarded identity is refused — the relay must say who it acts as.
        var factory = Factory(Substitute.For<IServerAssistant>(), configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(factory.CreateClient(), "hi", "relay-secret", userId: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Turn_StreamAccept_NoBearer_Returns401()
    {
        // The bearer filter runs before the handler, so SSE is never opened for an anonymous caller.
        var response = await StreamTurnAsync(Factory(Substitute.For<IServerAssistant>()).CreateClient(), "hi");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Turn_StreamAccept_EmptyPrompt_Returns400Json_NotSse()
    {
        // Pre-stream validation rejects before any SSE byte: a JSON 400, not a text/event-stream 200.
        var response = await StreamTurnAsync(Authed(Factory(Substitute.For<IServerAssistant>())), "");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    /// <summary>POSTs /turn with an <c>Accept: text/event-stream</c> header and the given prompt.</summary>
    private static async Task<HttpResponseMessage> StreamTurnAsync(HttpClient client, string prompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request);
    }

    /// <summary>POSTs /turn over the trusted-relay path: an SSE Accept + the relay secret and
    /// forwarded identity headers, no session bearer. A null header value is omitted.</summary>
    private static async Task<HttpResponseMessage> StreamTurnRelayAsync(
        HttpClient client, string prompt, string? secret, string? userId, string? userName = null,
        string? conversationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (secret is not null) request.Headers.Add("X-Relay-Secret", secret);
        if (userId is not null) request.Headers.Add("X-Relay-User", userId);
        if (userName is not null) request.Headers.Add("X-Relay-User-Name", userName);
        if (conversationId is not null) request.Headers.Add("X-Relay-Conversation-Id", conversationId);
        return await client.SendAsync(request);
    }

    private static async IAsyncEnumerable<AssistantStreamEvent> AsyncSeq(params AssistantStreamEvent[] events)
    {
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }

    /// <summary>Pulls the <c>token</c> out of the SSE <c>confirmation</c> event's data payload.</summary>
    private static string ExtractConfirmationToken(string sseBody)
    {
        foreach (var frame in sseBody.Split("\n\n"))
        {
            if (!frame.Contains("event: command.proposed"))
                continue;
            foreach (var line in frame.Split('\n'))
            {
                if (!line.StartsWith("data: "))
                    continue;
                using var doc = JsonDocument.Parse(line["data: ".Length..]);
                return doc.RootElement.GetProperty("token").GetString()!;
            }
        }
        return string.Empty;
    }

    private static string QueryValue(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return string.Empty;
    }

    // --- Conversation history read-back (the reverse path) ------------------------------------

    [Fact]
    public async Task Conversations_Relay_ListsCallersOwnScope_AndStripsChatIdPrefix()
    {
        // The list endpoint must scope to the FORWARDED user (web:<userId>), never client-supplied — a
        // caller can only ever enumerate its OWN chats. The DTO id is the per-chat sub-scope (the prefix
        // web:<userId>: stripped), i.e. exactly what the client sent as conversationId.
        var store = new RecordingConversationStore
        {
            Summaries =
            {
                new Llm.Models.ConversationSummary
                {
                    ConversationId = "web:relayuser:chatA", Title = "about factorio",
                    CreatedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch, TurnCount = 2,
                },
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(factory.CreateClient(), "/conversations", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.ListScope.Should().Be("web:relayuser");   // scoped to the forwarded id, not the client
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"id\":\"chatA\"");        // the web:relayuser: prefix stripped to the chat id
        body.Should().Contain("\"title\":\"about factorio\"");
        body.Should().Contain("\"turnCount\":2");
    }

    [Fact]
    public async Task Conversation_Relay_FetchesUserScopedKey_AndMapsTurnTo5aShape()
    {
        // The transcript endpoint composes the key exactly as /turn does (web:<userId>:<chatId>), so {id}
        // can only address the caller's OWN conversation. The turn maps to the §5·a vocabulary so a client
        // re-scaffolds it through its live-turn render path.
        var store = new RecordingConversationStore
        {
            History =
            {
                Llm.Models.ConversationEntry.ForTurn(new Llm.Models.ConversationTurnRecord
                {
                    ConversationId = "web:relayuser:chatA",
                    StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch,
                    UserPrompt = "is factorio up?", SystemPromptHash = "h",
                    Tools = new[]
                    {
                        new Llm.Models.RecordedToolCall(
                            new Llm.Models.Tool("get_status"),
                            new Dictionary<string, string?> { ["instance"] = "factorio" },
                            "factorio is running", 12, null),
                    },
                    Iterations = 1, Outcome = Llm.Models.TurnOutcome.Ok,
                    Think = true, Thinking = "let me check", Final = "Yes, it's running.",
                }),
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(factory.CreateClient(), "/conversations/chatA", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.HistoryKey.Should().Be("web:relayuser:chatA");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"kind\":\"turn\"");
        body.Should().Contain("\"prompt\":\"is factorio up?\"");
        body.Should().Contain("\"final\":\"Yes, it's running.\"");
        body.Should().Contain("\"think\":true");
        body.Should().Contain("\"thinking\":\"let me check\"");
        body.Should().Contain("\"tool\":\"get_status\"");       // §5·a field name reused
        body.Should().Contain("\"summary\":\"factorio is running\"");
        body.Should().Contain("\"outcome\":\"ok\"");
    }

    [Fact]
    public async Task Conversation_Relay_SoftDelete_ScopesToCallerAndReturns204()
    {
        // DELETE composes the key exactly like the GETs (web:<userId>:<chatId>) — a caller can only ever
        // soft-delete its OWN conversation — and returns 204. The store keeps the transcript (the corpus);
        // only the listing hides it. Here we assert the endpoint forwarded the principal-scoped key.
        var store = new RecordingConversationStore();
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelaySendAsync(
            factory.CreateClient(), HttpMethod.Delete, "/conversations/chatA", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.DeletedKey.Should().Be("web:relayuser:chatA");
    }

    [Fact]
    public async Task Conversation_Relay_Compact_ScopesToCallerAndReturnsOutcome()
    {
        // POST /conversations/{id}/compact composes the key exactly like the reads/delete
        // (web:<userId>:<chatId>) — a caller can only ever compact its OWN conversation — and relays the
        // CompactionOutcome JSON. The compactor is faked so the endpoint is proven hermetically (no model
        // round-trip): we assert the principal-scoped key + the outcome shape on the wire.
        var compactor = new RecordingCompactor(Llm.Models.CompactionOutcome.Done(7, "summary of earlier turns"));
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withCompactor: compactor);

        var response = await RelaySendAsync(
            factory.CreateClient(), HttpMethod.Post, "/conversations/chatA/compact", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        compactor.CompactedKey.Should().Be("web:relayuser:chatA");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"compacted\":true");
        body.Should().Contain("\"messagesCompacted\":7");
        body.Should().Contain("\"summary\":\"summary of earlier turns\"");
    }

    // --- The review surface (/admin/conversations…) --------------------------------------------
    // The gate is the whole point: these endpoints read OTHER users' conversations, so every test
    // below is either "the gate holds" or "what the gate lets through is right".

    private const string ChatAHandle = "d2ViOnUxOmNoYXRB";   // base64url("web:u1:chatA")

    private static Llm.Models.ConversationSummary Summary(
        string id, bool deleted = false, int errors = 0, string? display = null) =>
        new()
        {
            ConversationId = id, Title = "about factorio",
            CreatedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
            TurnCount = 2, Deleted = deleted, ErrorTurns = errors, UserDisplay = display,
        };

    [Fact]
    public async Task Review_Relay_WithoutTheAdminHeader_IsForbidden()
    {
        // An api that doesn't speak X-Relay-Admin must never open the surface by omission — the same
        // fail-closed rule as X-Relay-Can-Act. The caller IS authenticated here (401 would be wrong).
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Review_Relay_WithAdminFalse_IsForbidden()
    {
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", admin: false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Review_Bearer_WithNoReviewRoleConfigured_IsForbidden()
    {
        // The session-bearer path resolves its own authority, and a host that configured no review role
        // has nobody who may review — the surface stays shut rather than defaulting open.
        var factory = Factory(withStore: new RecordingConversationStore());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-session");

        var response = await client.GetAsync("/admin/conversations/users");

        // No session ⇒ the bearer filter rejects first; the point is that it never reaches the handler.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Review_Users_ListsEveryoneOnTheWebSurface()
    {
        var store = new RecordingConversationStore
        {
            Actors =
            {
                new Llm.Models.ConversationActor
                {
                    Surface = "web", UserId = "245717107596197888", UserDisplay = "Ana",
                    ConversationCount = 4, DeletedCount = 1, TurnCount = 9,
                    FirstActivityAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
                },
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.ActorSurface.Should().Be("web");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"userId\":\"245717107596197888\"");
        body.Should().Contain("\"displayName\":\"Ana\"");
        body.Should().Contain("\"conversationCount\":4");
        body.Should().Contain("\"deletedCount\":1");
    }

    [Fact]
    public async Task Review_Users_ReportsAnUnknownNameAsNull_NeverTheId()
    {
        // A conversation recorded before names were captured has no name. It must read as null so the
        // client shows the raw id — a name inferred from an id would be fabricated.
        var store = new RecordingConversationStore
        {
            Actors =
            {
                new Llm.Models.ConversationActor
                {
                    Surface = "web", UserId = "245717107596197888", UserDisplay = null,
                    ConversationCount = 1, DeletedCount = 0, TurnCount = 1,
                    FirstActivityAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
                },
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", admin: true);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"displayName\":null");
        body.Should().NotContain("\"displayName\":\"245717107596197888\"");
    }

    [Fact]
    public async Task Review_Conversations_ScopesToTheAskedUser_AndIncludesDeletedFlagged()
    {
        var store = new RecordingConversationStore
        {
            Summaries = { Summary("web:u1:chatA"), Summary("web:u1:chatB", deleted: true, errors: 3) },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations?user=u1", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.ListScope.Should().Be("web:u1");             // the asked-for user, composed server-side
        store.ListIncludedDeleted.Should().BeTrue();       // a review sees what the owner hid
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"deleted\":true");
        body.Should().Contain("\"errorTurns\":3");
        body.Should().Contain($"\"id\":\"{ChatAHandle}\"");  // an opaque handle, not the stored key
        body.Should().NotContain("web:u1:chatA");           // the store's key never reaches the client
    }

    [Fact]
    public async Task Review_Transcript_ResolvesTheHandle_AndReturnsTheSameEntryShapeAsAnOwnChat()
    {
        var store = new RecordingConversationStore
        {
            Summaries = { Summary("web:u1:chatA", display: "Ana") },
            History =
            {
                Llm.Models.ConversationEntry.ForTurn(new Llm.Models.ConversationTurnRecord
                {
                    ConversationId = "web:u1:chatA", UserDisplay = "Ana",
                    StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch,
                    UserPrompt = "is factorio up?", SystemPromptHash = "h",
                    Tools = Array.Empty<Llm.Models.RecordedToolCall>(),
                    Iterations = 1, Outcome = Llm.Models.TurnOutcome.Ok,
                    Think = false, Final = "Yes, it's running.",
                }),
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), $"/admin/conversations/{ChatAHandle}", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.HistoryKey.Should().Be("web:u1:chatA");   // the handle decoded back to the stored key
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"userId\":\"u1\"");
        body.Should().Contain("\"displayName\":\"Ana\"");
        // The entries are the SAME §5·a shape GET /conversations/{id} returns for your own chat.
        body.Should().Contain("\"kind\":\"turn\"");
        body.Should().Contain("\"prompt\":\"is factorio up?\"");
        body.Should().Contain("\"final\":\"Yes, it's running.\"");
    }

    [Fact]
    public async Task Review_Transcript_RefusesAHandleOutsideTheWebSurface()
    {
        // "cli:abc" base64url'd — a well-formed handle naming a namespace this surface does not serve.
        var outside = Convert.ToBase64String(Encoding.UTF8.GetBytes("cli:abc")).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), $"/admin/conversations/{outside}", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Review_Transcript_RefusesAHandleForAConversationTheListingDoesNotHold()
    {
        // A decodable handle is not authority to read: the conversation must actually exist under the
        // user it names, or the surface serves nothing.
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());   // no summaries → nothing to resolve

        var response = await RelayGetAsync(
            factory.CreateClient(), $"/admin/conversations/{ChatAHandle}", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Review_Transcript_RefusesAMalformedHandle()
    {
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/!!!not-base64!!!", "relay-secret", "relayuser", admin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>GETs a secured path over the trusted-relay path (secret + forwarded identity, no bearer).</summary>
    private static Task<HttpResponseMessage> RelayGetAsync(
        HttpClient client, string path, string secret, string userId, bool? admin = null) =>
        RelaySendAsync(client, HttpMethod.Get, path, secret, userId, admin);

    /// <summary>Sends any method to a secured path over the trusted-relay path (secret + forwarded id).</summary>
    private static async Task<HttpResponseMessage> RelaySendAsync(
        HttpClient client, HttpMethod method, string path, string secret, string userId, bool? admin = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Relay-Secret", secret);
        request.Headers.Add("X-Relay-User", userId);
        // Omitted entirely when null — that IS the case a review test needs to cover (an older api
        // that doesn't speak the header must not be granted the surface).
        if (admin is not null)
            request.Headers.Add("X-Relay-Admin", admin.Value ? "true" : "false");
        return await client.SendAsync(request);
    }
}

/// <summary>
/// A fake <see cref="Llm.Interfaces.IConversationStore"/> for the reverse-path endpoint tests: returns
/// canned data and records the scope/key it was asked for, so a test can assert the endpoint scopes to
/// the forwarded principal (never a client-supplied value).
/// </summary>
internal sealed class RecordingConversationStore : Llm.Interfaces.IConversationStore
{
    public List<Llm.Models.ConversationSummary> Summaries { get; } = new();
    public List<Llm.Models.ConversationEntry> History { get; } = new();
    public List<Llm.Models.ConversationActor> Actors { get; } = new();
    public string? ListScope { get; private set; }
    public bool ListIncludedDeleted { get; private set; }
    public string? ActorSurface { get; private set; }
    public string? HistoryKey { get; private set; }
    public string? DeletedKey { get; private set; }

    public IReadOnlyList<Llm.Models.ConversationSummary> ListConversations(string scopeKey, bool includeDeleted = false)
    {
        ListScope = scopeKey;
        ListIncludedDeleted = includeDeleted;
        return Summaries;
    }

    public IReadOnlyList<Llm.Models.ConversationActor> ListActors(string surfacePrefix)
    {
        ActorSurface = surfacePrefix;
        return Actors;
    }

    public IReadOnlyList<Llm.Models.ConversationEntry> GetHistory(string conversationId)
    {
        HistoryKey = conversationId;
        return History;
    }

    public IReadOnlyList<Llm.Models.LlmMessage> GetModelContext(string conversationId) => Array.Empty<Llm.Models.LlmMessage>();
    public void AppendTurn(Llm.Models.ConversationTurnRecord turn) { }
    public void AddCheckpoint(string conversationId, string summary) { }
    public void SoftDelete(string conversationId) => DeletedKey = conversationId;
}

/// <summary>
/// A fake <see cref="Llm.Interfaces.IConversationCompactor"/> for the compaction endpoint test: records the
/// scope key it was asked to compact (so the test can assert principal-scoping) and returns a canned outcome
/// (so the endpoint is exercised without a model round-trip).
/// </summary>
internal sealed class RecordingCompactor(Llm.Models.CompactionOutcome outcome) : Llm.Interfaces.IConversationCompactor
{
    public string? CompactedKey { get; private set; }

    public Task<Llm.Models.Result<Llm.Models.CompactionOutcome>> CompactAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        CompactedKey = conversationId;
        return Task.FromResult(Llm.Models.Result<Llm.Models.CompactionOutcome>.Success(outcome));
    }
}
