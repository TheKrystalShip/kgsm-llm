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
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;
using static TheKrystalShip.Kgsm.Assistant.Service.Tests.AuthStubs;

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
        ISignInService? discord = null,
        Action<IWebHostBuilder>? configure = null,
        Llm.Interfaces.IConversationStore? withStore = null,
        Llm.Interfaces.IConversationCompactor? withCompactor = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            // Provide kgsm settings so app startup binds the KGSM section regardless of
            // whether the test host discovers the service's appsettings.json.
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            // A stable signing key, so a bearer minted for a test survives into the request that
            // presents it (a blank key is per-process ephemeral, which is fine but noisier to reason
            // about), and durable state confined to this run.
            builder.UseSetting("Auth:SigningKey", "endpoint-smoke-signing-key");
            // ⚠ Never the default. /var/lib/kgsm/auth/users.db is the HOST's real account store,
            // shared with every KGSM service on the box, and opening it CREATES it — so an unpinned
            // test run would hand the operator a live accounts file that nobody made.
            builder.UseSetting("Auth:UsersDbPath",
                Path.Combine(Path.GetTempPath(), $"kgsm-assistant-tests-users-{Guid.NewGuid():N}.db"));
            builder.UseSetting("Auth:HostId", "test-host");
            builder.UseSetting("Conversation:DatabasePath", DatabasePath);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                // A confirmed lifecycle command watches the run state for its postcondition; the real
                // window is 90 seconds. A test whose spied engine never reports the server running would
                // otherwise wait out that whole window, so the suite uses a millisecond one.
                services.RemoveAll<SettlementTiming>();
                services.AddSingleton(new SettlementTiming(
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));
                if (assistant is not null) services.AddSingleton(assistant);
                // Both halves of the sign-in come off the one substitute, so a test that stubs the
                // authority and a handler that resolves it are talking about the same object.
                if (discord is not null)
                {
                    services.AddSingleton(discord);
                    services.AddSingleton((IAuthorityProvider)discord);
                }
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

    /// <summary>This run's durable state, so sessions from one test run never reach the next.</summary>
    private static readonly string DatabasePath = Path.Combine(
        Path.GetTempPath(), "kgsm-endpoint-smoke-" + Guid.NewGuid().ToString("N"), "conversations.db");

    /// <summary>
    /// A client carrying a real session bearer for <paramref name="userId"/> — minted by the app's own
    /// token service and backed by a real session row, so the request travels the same validation path
    /// a browser's would rather than a shortcut only tests can take.
    /// </summary>
    private static async Task<HttpClient> AuthedAsync(
        WebApplicationFactory<Program> factory, string userId = "user1", KgsmTier tier = KgsmTier.Viewer)
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new KgsmIdentity(KgsmActorProvider.Discord, userId, userId, "User One", null, ["identify"]);
        string sessionId = "sid_" + Guid.NewGuid().ToString("N");
        MintedToken access = tokens.MintAccess(identity, tier, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, userId, "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "tests", "jti_seed"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
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
        var client = await AuthedAsync(Factory(Substitute.For<IServerAssistant>()));

        var response = await client.PostAsJsonAsync("/turn", new { prompt = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Turn_Valid_DerivesPrincipalConversationId_AndReturnsText()
    {
        var assistant = Substitute.For<IServerAssistant>();
        // conversationId is derived server-side from the principal — NOT from the request.
        assistant.RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(AssistantResult.Ok("hello from assistant", Array.Empty<PendingConfirmation>())));

        var client = await AuthedAsync(Factory(assistant));
        var response = await client.PostAsJsonAsync("/turn", new { prompt = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TurnResponse>();
        body!.Text.Should().Be("hello from assistant");
        body.Confirmations.Should().BeEmpty();
        await assistant.Received(1).RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Confirm_GarbageToken_Returns400()
    {
        var client = await AuthedAsync(Factory(Substitute.For<IServerAssistant>()));

        var response = await client.PostAsJsonAsync("/confirm", new { token = "not-a-valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_TokenStagedByAnotherUser_IsRejected()
    {
        // A confirmation token bound to "alice" must not be confirmable by "bob".
        var factory = Factory(Substitute.For<IServerAssistant>(),
            configure: b => b.UseSetting("Assistant:ActionsEnabled", "true"));
        var bob = await AuthedAsync(factory, "bob");

        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var aliceToken = pending.Put(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), "alice", DateTimeOffset.UtcNow.AddMinutes(5));

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
        // The confirm settles against the observed run state, so the engine has to report the server
        // actually up — an accepted start alone is no longer an outcome.
        instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["inst"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "inst", Status = true }),
        });

        // canPerformActions is re-derived live from the principal's guild role at confirm time.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IInstanceService>(instances));
        });

        var client = await AuthedAsync(factory); // userId=user1, DisplayName="User One"
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(new PendingConfirmation(ConfirmationKind.Start, "inst"), "user1", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await client.PostAsJsonAsync("/confirm", new { token });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The engine call was attributed to the confirming Discord user, via the assistant surface —
        // NOT the bare OS-user fallback (which would be null, null → unattributed audit row).
        instances.Received(1).Start("inst", "discord:User One", "assistant");

        // The wire carries the verdict, not just a sentence: the postcondition was observed, so this
        // is `settled` and the observed state travels with it.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        var outcome = body.GetProperty("outcome");
        outcome.GetProperty("verdict").GetString().Should().Be("settled");
        outcome.GetProperty("observedState").GetString().Should().Be("running");
        outcome.GetProperty("instance").GetString().Should().Be("inst");
    }

    [Fact]
    public async Task Confirm_Start_EngineAcceptsButServerNeverComesUp_IsNotASuccess()
    {
        // The defect this closes: kgsm's `lifecycle start` returns as soon as the watchdog accepts the
        // spawn, so a zero exit code said "started" for a server that never came up. The confirm now
        // reports what it observed, and a client that renders `success` gets the truth.
        var instances = Substitute.For<IInstanceService>();
        instances.GetAll().Returns(new Dictionary<string, Instance>
        {
            ["inst"] = new Instance { Name = "inst", BlueprintFile = "factorio" },
        });
        instances.Start("inst", Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0));
        instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["inst"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "inst", Status = false }),
        });

        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IInstanceService>(instances));
        });

        var client = await AuthedAsync(factory);
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(new PendingConfirmation(ConfirmationKind.Start, "inst"), "user1", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await client.PostAsJsonAsync("/confirm", new { token });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        instances.Received(1).Start("inst", "discord:User One", "assistant");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("outcome").GetProperty("verdict").GetString().Should().Be("notSettled");
        body.GetProperty("outcome").GetProperty("observedState").GetString().Should().Be("stopped");
    }

    [Fact]
    public async Task Confirm_Start_RunStateUnreadable_IsUnknown_NotStopped()
    {
        // "We could not look" must never render as "it is not running" — the two are different facts and
        // the second one is a fabrication.
        var instances = Substitute.For<IInstanceService>();
        instances.GetAll().Returns(new Dictionary<string, Instance>
        {
            ["inst"] = new Instance { Name = "inst", BlueprintFile = "factorio" },
        });
        instances.Start("inst", Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0));
        instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["inst"] = Reading<InstanceRuntimeStatus>.Unavailable("the status source is offline", ReadingCode.MonitorOffline),
        });

        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IInstanceService>(instances));
        });

        var client = await AuthedAsync(factory);
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(new PendingConfirmation(ConfirmationKind.Start, "inst"), "user1", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await client.PostAsJsonAsync("/confirm", new { token });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("outcome").GetProperty("verdict").GetString().Should().Be("unknown");
        body.GetProperty("outcome").GetProperty("observedState").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task Confirm_Relay_BlueprintFinalize_CanActHeaderGrantsAuthority()
    {
        // The blueprint-review Save arrives on the trusted-relay path (kgsm-api). Authority MUST come from
        // X-Relay-Tier exactly as the propose side does — a relay host with no Discord config has no bot
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
        });
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(
            new PendingConfirmation(ConfirmationKind.Blueprint, "satisfactory", InstanceName: "Satisfactory"),
            "relayuser", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await RelayConfirmAsync(factory.CreateClient(), token, "edited-yaml", "relay-secret", "relayuser", "operator");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await assistant.Received(1).FinalizeBlueprintAsync("Satisfactory", "edited-yaml", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("viewer")]   // authenticated, but below operator
    [InlineData(null)]       // a relay that does not speak the header at all
    public async Task Confirm_Relay_BelowOperator_IsDenied(string? tier)
    {
        // Fail-closed, both ways round: a tier under operator and an ABSENT tier must both reach the
        // finalize with canPerform=false. Omission is the one that matters — a relay that says nothing
        // must never be read as saying yes.
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
        });
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(
            new PendingConfirmation(ConfirmationKind.Blueprint, "satisfactory", InstanceName: "Satisfactory"),
            "relayuser", DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await RelayConfirmAsync(factory.CreateClient(), token, "edited-yaml", "relay-secret", "relayuser", tier);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await assistant.Received(1).FinalizeBlueprintAsync("Satisfactory", "edited-yaml", false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("operator")]      // authenticated, but review is admin's
    [InlineData("not-a-tier")]    // a spelling this service does not know
    [InlineData("")]              // present and empty
    public async Task Review_Relay_WithoutAdminTier_IsForbidden(string tier)
    {
        // The parse is fail-closed, so an unrecognised or empty spelling denies exactly as a real
        // lower tier does. A relay cannot open the review surface by sending something unexpected.
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", tier);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>POSTs /confirm over the trusted-relay path with the relay secret + forwarded identity and,
    /// optionally, the <c>X-Relay-Tier</c> authority header. <paramref name="tier"/> null omits it.</summary>
    private static async Task<HttpResponseMessage> RelayConfirmAsync(
        HttpClient client, string token, string editedContent, string secret, string userId, string? tier)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/confirm")
        {
            Content = JsonContent.Create(new { token, editedContent }),
        };
        request.Headers.Add("X-Relay-Secret", secret);
        request.Headers.Add("X-Relay-User", userId);
        if (tier is not null) request.Headers.Add("X-Relay-Tier", tier);
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

    /// <summary>
    /// Walks a full sign-in the way a browser does — follow the 302 to the (fake) Discord, carry the
    /// handshake cookie back to the callback — and returns the callback's response.
    /// </summary>
    private static async Task<HttpResponseMessage> SignInAsync(
        WebApplicationFactory<Program> factory, string code = "the-code", string? forgedState = null,
        string? returnTo = null, string? discordError = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var startUrl = returnTo is null
            ? "/auth/discord/start"
            : $"/auth/discord/start?return_to={Uri.EscapeDataString(returnTo)}";
        var start = await client.GetAsync(startUrl);
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Carry back every cookie the start leg set — the handshake, and (in browser mode) the return
        // address, exactly as a browser would.
        string cookies = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        string state = forgedState ?? QueryValue(start.Headers.Location!.ToString(), "state");

        // Discord returns EITHER a code or an error, never both — model that rather than sending a
        // callback shaped like nothing Discord would ever produce.
        string query = discordError is null
            ? $"code={code}&state={state}"
            : $"error={Uri.EscapeDataString(discordError)}&state={state}";
        var callback = new HttpRequestMessage(HttpMethod.Get, $"/auth/discord/callback?{query}");
        callback.Headers.Add("Cookie", cookies);
        return await client.SendAsync(callback);
    }

    // --- the browser return leg (a client asking to be sent back with the session) ----------------

    /// <summary>A factory whose allowlist contains <paramref name="origin"/>, with a fake Discord.</summary>
    /// <remarks>
    /// The tier is written onto the ACCOUNT the arriving identity proves, not onto the fake. A provider
    /// says who someone is and contributes nothing else, so a sign-in test that wants a particular tier
    /// has to say what this host's record says about that person.
    /// </remarks>
    private WebApplicationFactory<Program> ReturnLegFactory(string origin, KgsmTier tier = KgsmTier.Operator)
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedPrincipal(
                new KgsmIdentity(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]), tier));

        WebApplicationFactory<Program> factory = Factory(
            discord: discord, configure: b => b.UseSetting("Auth:AllowedOrigins:0", origin));
        if (tier != KgsmTier.None)
            GiveAnAccount(factory, "u1", tier);
        return factory;
    }

    /// <summary>Give a Discord subject an approved account at <paramref name="tier"/> on this host.</summary>
    private static void GiveAnAccount(
        WebApplicationFactory<Program> factory, string subject, KgsmTier tier)
    {
        var users = factory.Services.GetRequiredService<Security.UserDirectory>();
        var identity = new KgsmIdentity(
            KgsmActorProvider.Discord, subject, "user" + subject, "User", null, []);
        users.Linking.ProvisionAsync(
            identity, tier, TierSource.Granted, UserStatus.Active, DateTimeOffset.UtcNow)
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task StaticFiles_DoNotShadowTheApiRoutes()
    {
        // The web client is served from the ROOT, where every endpoint also lives. Static middleware
        // serves only files that exist, so an API path falls through — but that is a property of the
        // pipeline's shape (no SPA fallback), and a fallback added later would silently turn every
        // unmatched path into a 200 with an HTML body. Pinned so it cannot be added by accident.
        HttpClient client = Factory().CreateClient();

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Unauthenticated, but ROUTED — a 401 proves the endpoint answered rather than a file.
        (await client.PostAsJsonAsync("/turn", new { prompt = "hi" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/confirm", new { token = "t" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        // A path that is neither an endpoint nor a file is still a 404, not an index.html.
        (await client.GetAsync("/definitely-not-a-route")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("consent_required")]     // prompt=none, but this app has not been authorized yet
    [InlineData("login_required")]       // prompt=none, but nobody is signed in at Discord
    public async Task Callback_DiscordDeclined_CarriesItsOwnReasonBack(string reason)
    {
        // A silent sign-in that needs a human is a DIFFERENT outcome from a broken callback, and a
        // client that cannot tell them apart has to guess between retrying visibly and giving up.
        const string origin = "https://panel.example.com";
        var response = await SignInAsync(ReturnLegFactory(origin), returnTo: origin, discordError: reason);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        string location = response.Headers.Location!.ToString();
        location.Should().StartWith(origin);
        ParseFragment(location)["error"].Should().Be(reason);
    }

    [Fact]
    public async Task Callback_DiscordDeclined_WithAForgedState_IsStillRefusedAsForged()
    {
        // An error response carries `state` too. Echoing its reason back before checking the state
        // would let anyone drive this endpoint's answer without ever having started a sign-in here.
        const string origin = "https://panel.example.com";
        var response = await SignInAsync(
            ReturnLegFactory(origin), forgedState: "not-the-issued-state", returnTo: origin,
            discordError: "consent_required");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ParseFragment(response.Headers.Location!.ToString())["error"].Should().Be("invalid_state");
    }

    [Fact]
    public async Task SignIn_WithReturnTo_RedirectsBackWithTheSessionInTheFragment()
    {
        // The session rides the FRAGMENT, not the query: a fragment is never sent to a server, kept in
        // a Referer header, or written to an access log.
        const string origin = "https://panel.example.com";
        var response = await SignInAsync(ReturnLegFactory(origin), returnTo: origin + "/chat");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith(origin + "/chat#");
        location.Should().NotContain("?access=");

        var frag = ParseFragment(location);
        frag.Should().ContainKey("access");
        frag.Should().ContainKey("refresh");
        frag["tier"].Should().Be("operator");
        frag["access"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SignIn_WithReturnTo_WhenDenied_RedirectsWithAnErrorFragment()
    {
        // A denial must come back the same way a success would; a browser that asked to be returned
        // must never be dead-ended on a JSON body it cannot act on.
        const string origin = "https://panel.example.com";
        WebApplicationFactory<Program> factory = ReturnLegFactory(origin, KgsmTier.None);
        // The one terminal refusal left: a fact about the account, not about a guild.
        GiveAnAccount(factory, "u1", KgsmTier.Viewer);
        var users = factory.Services.GetRequiredService<Security.UserDirectory>();
        var account = await users.Store.FindByCredentialAsync("discord:u1");
        await users.Store.UpdateAsync(account! with { Status = UserStatus.Disabled });

        var response = await SignInAsync(factory, returnTo: origin);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ParseFragment(response.Headers.Location!.ToString())["error"].Should().Be("denied");
    }

    [Fact]
    public async Task SignIn_WithReturnTo_ForgedState_RedirectsWithAnErrorFragment_AndNeverExchanges()
    {
        const string origin = "https://panel.example.com";
        var factory = ReturnLegFactory(origin);
        var response = await SignInAsync(factory, forgedState: "not-the-issued-state", returnTo: origin);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ParseFragment(response.Headers.Location!.ToString())["error"].Should().Be("invalid_state");
    }

    [Fact]
    public async Task Start_WithAnUnlistedReturnTo_Is400_AndNeverBouncesToDiscord()
    {
        // Refused at the START, not the callback: bouncing to Discord for a login that cannot be
        // completed spends the user's consent on a dead end.
        var client = ReturnLegFactory("https://panel.example.com")
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            "/auth/discord/start?return_to=" + Uri.EscapeDataString("https://evil.example.com/steal"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Callback_WithAHandCraftedReturnCookie_IsNotRedirectedToIt()
    {
        // The cookie is client-held and carries no integrity of its own, so the allowlist is checked
        // again here. Without this second check, setting one cookie by hand turns the callback into an
        // open redirect that hands over a real session.
        const string origin = "https://panel.example.com";
        var factory = ReturnLegFactory(origin);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await client.GetAsync("/auth/discord/start");
        string handshake = start.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("kgsm_oauth_state=", StringComparison.Ordinal)).Split(';')[0];
        string state = QueryValue(start.Headers.Location!.ToString(), "state");

        var callback = new HttpRequestMessage(HttpMethod.Get, $"/auth/discord/callback?code=the-code&state={state}");
        callback.Headers.Add("Cookie", $"{handshake}; kgsm_oauth_return=https://evil.example.com/steal");
        var response = await client.SendAsync(callback);

        // Falls back to the JSON contract — the session is minted, but it is handed to the caller, not
        // flung at an origin nobody allowed.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Cors_Preflight_AllowsDelete()
    {
        // A client owns its conversations, and removing one is a DELETE. Without it a cross-origin
        // client can accumulate a history it has no way to clear.
        const string origin = "https://example.github.io";
        var client = Factory(configure: b => b.UseSetting("Auth:AllowedOrigins:0", origin)).CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/conversations/abc");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "DELETE");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(origin);
        response.Headers.GetValues("Access-Control-Allow-Methods")
            .SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries))
            .Should().Contain("DELETE");
    }

    /// <summary>Splits a URL's <c>#a=1&amp;b=2</c> fragment into a map, percent-decoding values.</summary>
    private static Dictionary<string, string> ParseFragment(string url)
    {
        var hash = url.IndexOf('#');
        if (hash < 0) return [];
        return url[(hash + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
    }

    [Fact]
    public async Task SignIn_MintsAPairAtTheTierTheAccountHolds()
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedPrincipal(
                new KgsmIdentity(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]), KgsmTier.Admin));

        WebApplicationFactory<Program> factory = Factory(discord: discord);
        // The tier comes off the account, and the fake's claim of admin is ignored — which is the
        // whole assertion below the obvious one.
        GiveAnAccount(factory, "u1", KgsmTier.Viewer);
        var response = await SignInAsync(factory);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        session!.Verdict.Should().Be("ok");
        session.Token.Should().NotBeNullOrEmpty();
        session.Refresh.Should().NotBeNullOrEmpty();
        session.Tier.Should().Be("viewer");
        session.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task SignIn_WithAForgedState_IsRefusedBeforeAnyExchange()
    {
        // The CSRF gate. Without it an attacker sends the victim a callback link carrying the
        // ATTACKER'S code, and the victim's browser is handed a session for the attacker's identity.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://discord.test/authorize");

        var response = await SignInAsync(Factory(discord: discord), forgedState: "not-the-issued-state");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await discord.DidNotReceive().ResolveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_WithoutTheHandshakeCookie_IsRefused()
    {
        // A callback arriving with no cookie is a login that did not start in this browser — the exact
        // request the state check exists to reject, and the reason the state lives in a cookie at all.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://discord.test/authorize?state=abc");

        var client = Factory(discord: discord).CreateClient();
        var response = await client.GetAsync("/auth/discord/callback?code=the-code&state=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await discord.DidNotReceive().ResolveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_WithNoAccountHere_ProvisionsOneAwaitingApproval()
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedPrincipal(
                new KgsmIdentity(KgsmActorProvider.Discord, "u9", "stranger", "Stranger", null, ["identify"]), KgsmTier.None));

        WebApplicationFactory<Program> factory = Factory(discord: discord);
        var response = await SignInAsync(factory);

        // Proving who you are is not being let in — and it is not being turned away either. A real
        // session holding nothing is what lets the chat say "waiting on an admin" rather than showing
        // somebody who just proved who they are a bare denial.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        session!.Verdict.Should().Be("ok");
        session.Tier.Should().Be("none");
        session.Token.Should().NotBeNullOrEmpty();

        var users = factory.Services.GetRequiredService<Security.UserDirectory>();
        var provisioned = await users.Store.FindByCredentialAsync("discord:u9");
        provisioned.Should().NotBeNull();
        provisioned!.Status.Should().Be(UserStatus.Pending);
    }

    [Fact]
    public async Task SignIn_WhenDiscordIsUnreachable_Is502NotADenial()
    {
        // "We could not ask" must never be reported as an answer — collapsing the two either locks out
        // a real admin during an outage or, far worse, admits someone during one.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ResolvedPrincipal?>(_ => throw new DiscordAuthException("unreachable"));

        var response = await SignInAsync(Factory(discord: discord));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task SignIn_WithABadCode_Is401()
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ResolvedPrincipal?)null);

        var response = await SignInAsync(Factory(discord: discord));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARefreshTokenBuysAWorkingBearer()
    {
        // The whole point of the refresh lane: a lapsed access token is replaced without sending the
        // user back through Discord.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        discord.BuildAuthorizeUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"https://discord.test/authorize?state={ci.ArgAt<string>(0)}");
        discord.ResolveAsync("the-code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedPrincipal(
                new KgsmIdentity(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]), KgsmTier.Viewer));
        StubTier(discord, KgsmTier.Viewer);

        var factory = Factory(discord: discord);
        var signIn = await (await SignInAsync(factory)).Content.ReadFromJsonAsync<AuthSessionResponse>();

        var client = factory.CreateClient();
        var refreshed = await client.PostAsJsonAsync("/auth/session/refresh", new { refresh = signIn!.Refresh });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);

        var next = await refreshed.Content.ReadFromJsonAsync<AuthSessionResponse>();
        next!.Token.Should().NotBe(signIn.Token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.Token);
        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me");
        me!.UserId.Should().Be("u1");
    }

    [Fact]
    public async Task Refresh_WithoutAToken_Is400()
    {
        var response = await Factory().CreateClient().PostAsJsonAsync("/auth/session/refresh", new { refresh = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_WithGarbage_Is401()
    {
        var response = await Factory().CreateClient()
            .PostAsJsonAsync("/auth/session/refresh", new { refresh = "not-a-token" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoggingOutStopsTheBearerWorking()
    {
        // A signed token stays cryptographically valid until it expires, so signing out only means
        // something because the session behind it is checked on every request.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Viewer);

        var factory = Factory(discord: discord);
        var client = await AuthedAsync(factory);

        (await client.GetAsync("/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ABearerForAnUnknownSessionIsRefused()
    {
        // A validly-signed token whose session row is gone — a wiped database, or a token minted by a
        // host that no longer has it. Nothing can revoke a session it cannot find, so it does not pass.
        var factory = Factory();
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        MintedToken orphan = tokens.MintAccess(
            new KgsmIdentity(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]),
            KgsmTier.Admin, "sid_never_recorded");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", orphan.Token);

        (await client.GetAsync("/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARefreshTokenIsNotAcceptedAsABearer()
    {
        // It lives far longer than an access token; accepting one here would erase the short lifetime
        // that bounds privilege.
        var factory = Factory();
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new KgsmIdentity(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]);
        const string sessionId = "sid_refresh_as_bearer";
        MintedToken refresh = tokens.MintRefresh(identity, KgsmTier.Admin, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, "u1", "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null, refresh.Jti));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refresh.Token);

        (await client.GetAsync("/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_ReflectsLiveRoleLookup()
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
        });
        var client = await AuthedAsync(factory);

        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me");

        me!.UserId.Should().Be("user1");
        me.Tier.Should().Be("operator");
        me.CanPerformActions.Should().BeTrue();
    }

    [Fact]
    public async Task Me_ReportsTheLiveTierNotTheOneTheTokenWasMintedWith()
    {
        // Authority is re-derived rather than read off the bearer, so a role granted since sign-in is
        // already in effect — reporting the token's stale snapshot would contradict the next request.
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord);
        var client = await AuthedAsync(factory, tier: KgsmTier.Viewer);

        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me");

        me!.Tier.Should().Be("operator");
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
        assistant.RunStreamAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Hel"),
                AssistantStreamEvent.Token("lo"),
                AssistantStreamEvent.Final("Hello")));

        var response = await StreamTurnAsync(await AuthedAsync(Factory(assistant)), "hi");

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
        assistant.RunStreamAsync("web:user1", "status?", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(LlmTools.ServerInfo, new Dictionary<string, string?> { ["instance_name"] = "factorio" }, "tc_0"),
                AssistantStreamEvent.ToolResult(LlmTools.ServerInfo, "factorio: stopped", "tc_0"),
                AssistantStreamEvent.Token("Stopped."),
                AssistantStreamEvent.Final("Stopped.")));

        var response = await StreamTurnAsync(await AuthedAsync(Factory(assistant)), "status?");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: tool.start");
        body.Should().Contain("\"type\":\"tool.start\"");
        // §5·a correlation id — the SAME id rides tool.start and tool.result so a renderer pairs them.
        body.Should().Contain("\"id\":\"tc_0\"");
        body.Should().Contain("\"tool\":\"server_info\"");
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
        assistant.RunStreamAsync("web:user1", "health?", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(
                    LlmTools.RunHealthCheck, new Dictionary<string, string?> { ["instance_name"] = "factorio" }, "tc_0"),
                AssistantStreamEvent.ToolResult(
                    LlmTools.RunHealthCheck, "factorio: passed with warnings.", "tc_0", card),
                AssistantStreamEvent.Final("factorio has an update available.")));

        var response = await StreamTurnAsync(await AuthedAsync(Factory(assistant)), "health?");
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
        assistant.RunStreamAsync("web:user1", "make me a rust server", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.ToolStart(LlmTools.CreateBlueprint, new Dictionary<string, string?> { ["game"] = "Rust" }, "tc_0"),
                AssistantStreamEvent.Progress(LlmTools.CreateBlueprint, "research", "Looking it up online…"),
                AssistantStreamEvent.Progress(LlmTools.CreateBlueprint, "draft", "Building a server config…"),
                AssistantStreamEvent.ToolResult(LlmTools.CreateBlueprint, "Rust is now in the catalog.", "tc_0"),
                AssistantStreamEvent.Final("Rust is now in the catalog. Want me to make you a server?")));

        var response = await StreamTurnAsync(await AuthedAsync(Factory(assistant)), "make me a rust server");
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
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Staging…"),
                AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria")),
                AssistantStreamEvent.Final("Staged.")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:ActionsEnabled", "true"));
        var response = await StreamTurnAsync(await AuthedAsync(factory), "remove terraria");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: command.proposed");
        var token = ExtractConfirmationToken(body);

        // The token minted into the SSE frame must validate AND be bound to the caller (user1).
        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        pending.TryTake(token, "user1", out var confirmation).Should().BeTrue();
        confirmation.Kind.Should().Be(ConfirmationKind.Uninstall);
        confirmation.Target.Should().Be("terraria");
    }

    [Fact]
    public async Task Turn_StreamAccept_GeneralisedCommand_ProposedEventCarriesVerbAndSubject()
    {
        // A generalised command (start) is propose-only and surfaces as command.proposed carrying
        // `verb` (the normalised API token), `subject {resource,id}`, and a human `confirm` prompt.
        // The host-minted `token` rides alongside them for the /confirm surfaces.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Proposing…"),
                AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Start, "factorio")),
                AssistantStreamEvent.Final("Proposed.")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:ActionsEnabled", "true"));
        var response = await StreamTurnAsync(await AuthedAsync(factory), "start factorio");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: command.proposed");
        body.Should().Contain("\"type\":\"command.proposed\"");
        body.Should().Contain("\"verb\":\"start\"");          // normalised API verb, NOT the old `kind`
        body.Should().Contain("\"resource\":\"server\"");     // subject.resource
        body.Should().Contain("\"id\":\"factorio\"");         // subject.id (the resolved target)
        body.Should().Contain("\"confirm\":\"Start factorio?\"");

        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        pending.TryTake(ExtractConfirmationToken(body), "user1", out var confirmation).Should().BeTrue();
        confirmation.Kind.Should().Be(ConfirmationKind.Start);
        confirmation.Target.Should().Be("factorio");
    }

    [Fact]
    public async Task Turn_StreamAccept_ErrorEvent_EmitsCodeAndMessage()
    {
        // The error frame is a RESHAPE ({error} -> {code,message}), surfaced in-band on the
        // already-committed 200 stream (never a status code once the first frame has flushed).
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("…"),
                AssistantStreamEvent.Error("boom")));

        var response = await StreamTurnAsync(await AuthedAsync(Factory(assistant)), "do it");
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
        assistant.RunStreamAsync("web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        (await response.Content.ReadAsStringAsync()).Should().Contain("event: done");
        assistant.Received().RunStreamAsync(
            "web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Turn_Relay_ConversationId_SubScopesUserMemory()
    {
        // A per-chat X-Relay-Conversation-Id partitions the SAME user's memory into a fresh context
        // window — keyed web:<userId>:<chatId> — so a "new chat" no longer leaks the previous chat's
        // history, while staying strictly inside that user's namespace (the user id is the prefix).
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser:chat-abc123", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(
            factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User", "chat-abc123");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        assistant.Received().RunStreamAsync(
            "web:relayuser:chat-abc123", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
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
        var response = await StreamTurnAsync(await AuthedAsync(Factory(Substitute.For<IServerAssistant>())), "");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    // --- streamed /confirm (every kind, not just a blueprint finalize) ----------------------------

    [Fact]
    public async Task Confirm_StreamAccept_LifecycleCommand_StreamsTerminalResultFrame()
    {
        // A lifecycle confirm is now watched until it reaches its run state, so it can be a long silence
        // on one socket. Streaming carries the SAME ConfirmResponse a buffered caller gets, on a terminal
        // `result` frame, so the caller's card always reaches a terminal state.
        var (factory, token) = await StartConfirmFixtureAsync(running: true);

        var response = await StreamConfirmAsync(await AuthedAsync(factory), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: result");
        body.Should().Contain("\"type\":\"result\"");
        // The verdict travels on the streamed frame exactly as it does on the buffered response.
        body.Should().Contain("\"verdict\":\"settled\"");
        body.Should().Contain("\"observedState\":\"running\"");
        body.Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task Confirm_StreamAccept_SlowSettle_NarratesTheWait()
    {
        // A settle that has to wait says so, rather than leaving the client with nothing but heartbeats.
        // The step is reported only once the wait is real — a server already up settles on the first read
        // and narrates nothing.
        var (factory, token) = await StartConfirmFixtureAsync(running: false);

        var response = await StreamConfirmAsync(await AuthedAsync(factory), token);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: progress");
        body.Should().Contain("\"key\":\"settling\"");
        body.Should().Contain("Waiting for inst to come up");
        // …and it still reaches a terminal frame, carrying the honest non-success verdict.
        body.Should().Contain("event: result");
        body.Should().Contain("\"verdict\":\"notSettled\"");
        body.Should().Contain("\"success\":false");
    }

    [Fact]
    public async Task Confirm_StreamAccept_StaleToken_Returns400Json_NotSse()
    {
        // Token and payload are resolved BEFORE the stream opens, so a bad one is still a clean JSON 400
        // and never a 200 whose only failure signal is buried in an in-band frame.
        var factory = Factory();
        var response = await StreamConfirmAsync(await AuthedAsync(factory), "not-a-valid-token");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    /// <summary>
    /// A factory whose engine has one instance that will report <paramref name="running"/> after a start,
    /// plus a confirmation token for starting it.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Factory, string Token)> StartConfirmFixtureAsync(bool running)
    {
        var instances = Substitute.For<IInstanceService>();
        instances.GetAll().Returns(new Dictionary<string, Instance>
        {
            ["inst"] = new Instance { Name = "inst", BlueprintFile = "factorio" },
        });
        instances.Start("inst", Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0));
        instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["inst"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "inst", Status = running }),
        });

        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Operator);

        var factory = Factory(discord: discord, configure: b =>
        {
            b.UseSetting("Assistant:ActionsEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IInstanceService>(instances));
        });

        var pending = factory.Services.GetRequiredService<IPendingConfirmationStore>();
        var token = pending.Put(new PendingConfirmation(ConfirmationKind.Start, "inst"), "user1", DateTimeOffset.UtcNow.AddMinutes(5));
        return await Task.FromResult((factory, token));
    }

    /// <summary>POSTs /confirm with an <c>Accept: text/event-stream</c> header.</summary>
    private static async Task<HttpResponseMessage> StreamConfirmAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/confirm")
        {
            Content = JsonContent.Create(new { token }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request);
    }

    // ---- a caller's capability follows their AUTHORITY, not the transport ----------------------
    // Proposing an action is an operator capability, and the user confirms every proposal — so it must
    // not hang off the auto-run toggle. Gating it there is the difference between "the assistant offers
    // to start your server and waits" and "the assistant says it cannot start your server", for a user
    // who holds admin on the host.

    [Theory]
    [InlineData(null)]    // the field omitted entirely
    [InlineData(false)]   // auto-run explicitly off — the default in the panel
    public async Task Turn_Direct_MayProposeWithoutTheAutoRunToggle(bool? actions)
    {
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AsyncSeq(AssistantStreamEvent.Final("ok")));

        WebApplicationFactory<Program> factory = OperatorTurnFactory(assistant, out _);
        HttpClient client = await AuthedAsync(factory, ActionUser, KgsmTier.Admin);

        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt = "start factorio", actions }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync();   // drain the stream so the turn completes

        // canPerform TRUE (the caller is an operator), autoExecute FALSE (they did not ask for it).
        assistant.Received(1).RunStreamAsync(
            Arg.Any<string>(), "start factorio", true, Arg.Any<bool>(), false,
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Turn_Direct_AutoRunNeedsBothTheToggleAndAdmin()
    {
        // The toggle is intent; admin is authority. An operator who asks for auto-run still gets the
        // confirm-first path — the request can only ever narrow what the tier already allows.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AsyncSeq(AssistantStreamEvent.Final("ok")));

        WebApplicationFactory<Program> factory = OperatorTurnFactory(assistant, out ISignInService directory);
        HttpClient client = await AuthedAsync(factory, ActionUser, KgsmTier.Admin);

        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt = "start factorio", actions = true }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync();   // drain the stream so the turn completes

        assistant.Received(1).RunStreamAsync(
            Arg.Any<string>(), "start factorio", true, Arg.Any<bool>(), false,
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
        // The tier that mattered was re-derived from Discord, never read off the caller's token.
        await ((IAuthorityProvider)directory).Received().ResolveTierAsync(
            Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Turn_Direct_BelowOperator_MayNotPropose()
    {
        // Fail-closed on the axis that actually carries authority: no operator role, no proposals,
        // whatever the toggle says.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AsyncSeq(AssistantStreamEvent.Final("ok")));

        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(directory, KgsmTier.Viewer);   // in the guild, holding neither role
        WebApplicationFactory<Program> factory = Factory(assistant, directory, configure: ConfigureActionRoles);
        HttpClient client = await AuthedAsync(factory, ActionUser, KgsmTier.Admin);

        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt = "start factorio", actions = true }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync();   // drain the stream so the turn completes

        assistant.Received(1).RunStreamAsync(
            Arg.Any<string>(), "start factorio", false, Arg.Any<bool>(), false,
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    private const string ActionUser = "action-user";
    private const string ActionOperatorRole = "role-op";
    private const string ActionAdminRole = "role-admin";

    private static void ConfigureActionRoles(IWebHostBuilder b)
    {
        b.UseSetting("Assistant:ActionsEnabled", "true");
        b.UseSetting("Assistant:ActionsEnabled", "true");
    }

    /// <summary>A host where actions are enabled and <see cref="ActionUser"/> holds the OPERATOR role
    /// in the guild — enough to propose, not enough to auto-run.</summary>
    private WebApplicationFactory<Program> OperatorTurnFactory(
        IServerAssistant assistant, out ISignInService directory)
    {
        var stub = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(stub, KgsmTier.Operator);
        directory = stub;
        return Factory(assistant, stub, configure: ConfigureActionRoles);
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

    [Fact]
    public async Task Turn_Relay_LeafHeader_SelectsThatLeafsPrompts()
    {
        // The calling leaf reaches the turn, which is what makes a surface's own prompt and
        // tool-description overrides apply to it.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        var response = await StreamTurnRelayAsync(
            factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User", leaf: "kgsm-bot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        assistant.Received().RunStreamAsync(
            "web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), "kgsm-bot");
    }

    [Fact]
    public async Task Turn_Relay_NoLeafHeader_RunsAsTheAssistantsOwn()
    {
        // A relay that does not speak the header is unchanged by its existence — this is what keeps
        // the header additive rather than a coordinated deploy.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        await StreamTurnRelayAsync(factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User");

        assistant.Received().RunStreamAsync(
            "web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), null);
    }

    [Fact]
    public async Task Turn_Relay_MalformedLeafHeader_IsDropped_NotPassedOn()
    {
        // The leaf name becomes a path segment when prompts are resolved, so a name that would have
        // to be cleaned up to be usable is refused outright and the turn runs as the assistant's own.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"));
        await StreamTurnRelayAsync(
            factory.CreateClient(), "hi", "relay-secret", "relayuser", "Relay User", leaf: "../../etc");

        assistant.Received().RunStreamAsync(
            "web:relayuser", "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), null);
    }

    [Fact]
    public async Task Turn_LeafHeader_OnTheSessionPath_IsIgnored()
    {
        // The leaf is a fact the trusted relay asserts, not something a browser may claim: a session
        // caller sending it must not be able to pick another surface's prompts.
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync(Arg.Any<string>(), "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(_ => AsyncSeq(AssistantStreamEvent.Token("Hi"), AssistantStreamEvent.Final("Hi")));

        var factory = Factory(assistant);
        var client = await AuthedAsync(factory);
        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt = "hi" }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("X-Relay-Leaf", "kgsm-bot");
        await client.SendAsync(request);

        assistant.Received().RunStreamAsync(
            Arg.Any<string>(), "hi", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), null,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>(), null);
    }

    /// <summary>POSTs /turn over the trusted-relay path: an SSE Accept + the relay secret and
    /// forwarded identity headers, no session bearer. A null header value is omitted.</summary>
    private static async Task<HttpResponseMessage> StreamTurnRelayAsync(
        HttpClient client, string prompt, string? secret, string? userId, string? userName = null,
        string? conversationId = null, string? leaf = null)
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
        if (leaf is not null) request.Headers.Add("X-Relay-Leaf", leaf);
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
                            new Llm.Models.Tool("server_info"),
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
        body.Should().Contain("\"tool\":\"server_info\"");       // §5·a field name reused
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
    public async Task Conversation_Relay_Feedback_ScopesToCallerAndRecordsTheVerdict()
    {
        // The key is composed exactly like the reads/delete (web:<userId>:<chatId>), so a caller can only
        // ever rate a turn in its OWN conversation. The store's own ownership check is the second half of
        // that guard — entry ids are log-wide, so the route alone is not enough.
        var store = new RecordingConversationStore();
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayJsonAsync(
            factory.CreateClient(), "/conversations/chatA/turns/42/feedback", "relay-secret", "relayuser",
            """{"rating":"down","note":"named a server that doesn't exist"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.Feedback.Should().Be((
            "web:relayuser:chatA", 42L, (Llm.Models.TurnFeedbackRating?)Llm.Models.TurnFeedbackRating.Down,
            "named a server that doesn't exist"));
    }

    [Fact]
    public async Task Conversation_Relay_Feedback_DropsANoteLeftOnAThumbsUp()
    {
        // A note is the "what went wrong" behind a thumbs-down. Keeping one on a thumbs-up would file a
        // complaint against an answer its reader said was fine.
        var store = new RecordingConversationStore();
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayJsonAsync(
            factory.CreateClient(), "/conversations/chatA/turns/7/feedback", "relay-secret", "relayuser",
            """{"rating":"up","note":"ignore me"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.Feedback!.Value.Rating.Should().Be(Llm.Models.TurnFeedbackRating.Up);
        store.Feedback!.Value.Note.Should().BeNull();
    }

    [Fact]
    public async Task Conversation_Relay_Feedback_NullRatingWithdrawsTheVerdict()
    {
        var store = new RecordingConversationStore();
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayJsonAsync(
            factory.CreateClient(), "/conversations/chatA/turns/7/feedback", "relay-secret", "relayuser",
            """{"rating":null}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.Feedback!.Value.Rating.Should().BeNull();
    }

    [Fact]
    public async Task Conversation_Relay_Feedback_RejectsARatingThatIsNeitherThumb()
    {
        var store = new RecordingConversationStore();
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayJsonAsync(
            factory.CreateClient(), "/conversations/chatA/turns/7/feedback", "relay-secret", "relayuser",
            """{"rating":"sideways"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        store.Feedback.Should().BeNull();
    }

    [Fact]
    public async Task Conversation_Relay_Feedback_IsNotFoundWhenTheTurnIsNotTheCallers()
    {
        // The store refuses a turn id that is not part of the named conversation; the endpoint reports it
        // as unknown rather than confirming a write that did not happen.
        var store = new RecordingConversationStore { FeedbackAccepted = false };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayJsonAsync(
            factory.CreateClient(), "/conversations/chatA/turns/999/feedback", "relay-secret", "relayuser",
            """{"rating":"down"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
    public async Task Review_Relay_WithNoTierHeaderAtAll_IsForbidden()
    {
        // A relay that doesn't speak X-Relay-Tier must never open the surface by omission. The caller IS
        // authenticated here (401 would be wrong) — it is the authority that is absent, not the identity.
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
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", "operator");

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
    public async Task Review_Bearer_WhenDiscordCannotBeAsked_Is502WithAnAuthorityUnavailableEnvelope()
    {
        // The whole round trip a browser makes, with Discord down: a real session bearer, the real
        // gate, and the real response a client has to make sense of. It must not read as 403 — a panel
        // told "forbidden" reports a permissions problem for what is a transient upstream outage.
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTierUnreachable(directory);

        var factory = Factory(
            discord: directory,
            withStore: new RecordingConversationStore());

        var client = await AuthedAsync(factory, tier: KgsmTier.Admin);
        var response = await client.GetAsync("/admin/conversations/stats");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        // The envelope is the part clients branch on — a reverse proxy fronting a dead leaf answers
        // 502 too, with no body, and that case really is "the assistant isn't answering".
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("authority_unavailable");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Review_Bearer_WhenDiscordAnswersAndTheCallerIsNotAnAdmin_Is403()
    {
        // The counterpart, and the reason the two are worth telling apart: an answered question with a
        // negative answer stays a plain denial.
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(directory, KgsmTier.Operator);

        var factory = Factory(
            discord: directory,
            configure: b =>
            {
            },
            withStore: new RecordingConversationStore());

        var client = await AuthedAsync(factory, tier: KgsmTier.Admin);
        var response = await client.GetAsync("/admin/conversations/stats");

        // The bearer's own claim says admin and is ignored: authority is re-derived, and Discord says
        // this person is an operator.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", "admin");

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
            factory.CreateClient(), "/admin/conversations/users", "relay-secret", "relayuser", "admin");

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
            factory.CreateClient(), "/admin/conversations?user=u1", "relay-secret", "relayuser", "admin");

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
            factory.CreateClient(), $"/admin/conversations/{ChatAHandle}", "relay-secret", "relayuser", "admin");

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
            factory.CreateClient(), $"/admin/conversations/{outside}", "relay-secret", "relayuser", "admin");

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
            factory.CreateClient(), $"/admin/conversations/{ChatAHandle}", "relay-secret", "relayuser", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Review_Transcript_RefusesAMalformedHandle()
    {
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/!!!not-base64!!!", "relay-secret", "relayuser", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/conversations/stats — the corpus roll-up behind the operator overview. ──────────

    [Fact]
    public async Task Review_Stats_WithoutTheAdminHeader_IsForbidden()
    {
        // The roll-up describes other people's conversations in aggregate, so it sits behind the same
        // fail-closed admin gate as the transcripts themselves.
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/stats", "relay-secret", "relayuser");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Review_Stats_IsScopedToTheWebSurface_AndCarriesTheLiveRuntime()
    {
        // The runtime block is what makes the numbers legible: "median 2 steps" only means something
        // beside "the cap is 16", and a context percentage only means something beside its window.
        var store = new RecordingConversationStore
        {
            Stats = RecordingConversationStore.EmptyStats with
            {
                Conversations = 76, DeletedConversations = 46, Actors = 13, Turns = 159,
                OkTurns = 153, CancelledTurns = 3, UnrecordedOutcomeTurns = 3,
                MedianTurnMs = 5563, P95TurnMs = 37744, MaxTurnMs = 63613,
                MedianIterations = 2, MaxIterations = 6,
                MedianContextPercent = 9.5, MaxContextPercent = 21.2, ContextWindow = 32768,
                ThinkingTurns = 6, TurnsWithoutTool = 55, ToolCalls = 140,
            },
        };
        var factory = Factory(
            configure: b =>
            {
                b.UseSetting("Assistant:Relay:Secret", "relay-secret");
                b.UseSetting("Ollama:Model", "gemma4:12b");
                b.UseSetting("Ollama:NumCtx", "32768");
                b.UseSetting("LlmAgent:MaxIterations", "16");
            },
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/stats", "relay-secret", "relayuser", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.StatsSurface.Should().Be("web");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"conversations\":76");
        body.Should().Contain("\"turns\":159");
        body.Should().Contain("\"medianTurnMs\":5563");
        body.Should().Contain("\"contextWindow\":32768");
        body.Should().Contain("\"model\":\"gemma4:12b\"");
        body.Should().Contain("\"maxIterations\":16");
    }

    [Fact]
    public async Task Review_Stats_ReportsAnUnmeasuredDistributionAsNull_NeverZero()
    {
        // A corpus with nothing timed must say so. A zero median would render as "instant", which is a
        // fabricated measurement — the one thing this ecosystem never does.
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: new RecordingConversationStore());

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/stats", "relay-secret", "relayuser", "admin");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"medianTurnMs\":null");
        body.Should().Contain("\"p95TurnMs\":null");
        body.Should().Contain("\"medianContextPercent\":null");
        body.Should().Contain("\"contextWindow\":null");
        body.Should().Contain("\"turns\":0", "a count of something that did not happen IS zero");
    }

    [Fact]
    public async Task Review_Stats_MarksAToolTheCatalogDoesNotDefineAsUnknown()
    {
        // The model inventing a tool is the sharpest tuning signal in the corpus. The store reports the
        // name it recorded; deciding whether it exists happens here, where the catalog lives.
        var store = new RecordingConversationStore
        {
            Stats = RecordingConversationStore.EmptyStats with
            {
                Tools = new[]
                {
                    new Llm.Models.ToolStat { Name = "server_info", Calls = 20, MedianMs = 222, MaxMs = 1041, FailedCalls = 1 },
                    new Llm.Models.ToolStat { Name = "google_search", Calls = 1, MedianMs = 0, MaxMs = 0, FailedCalls = 1 },
                },
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/stats", "relay-secret", "relayuser", "admin");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"server_info\",\"known\":true");
        body.Should().Contain("\"name\":\"google_search\",\"known\":false");
    }

    [Fact]
    public async Task Review_Stats_CountsAConditionallyOfferedToolAsKnown()
    {
        // revise_blueprint is deliberately kept OUT of the ordinary-turn offer and appended only when a
        // draft is open. It is still a real tool, and checking against the offer set instead of the full
        // catalog reported it as invented — sending a reviewer after a bug that does not exist.
        var store = new RecordingConversationStore
        {
            Stats = RecordingConversationStore.EmptyStats with
            {
                Tools = new[]
                {
                    new Llm.Models.ToolStat { Name = "revise_blueprint", Calls = 5, MedianMs = 0, MaxMs = 8, FailedCalls = 0 },
                },
            },
        };
        var factory = Factory(configure: b => b.UseSetting("Assistant:Relay:Secret", "relay-secret"),
            withStore: store);

        var response = await RelayGetAsync(
            factory.CreateClient(), "/admin/conversations/stats", "relay-secret", "relayuser", "admin");

        (await response.Content.ReadAsStringAsync())
            .Should().Contain("\"name\":\"revise_blueprint\",\"known\":true");
    }

    /// <summary>GETs a secured path over the trusted-relay path (secret + forwarded identity, no bearer).</summary>
    private static Task<HttpResponseMessage> RelayGetAsync(
        HttpClient client, string path, string secret, string userId, string? tier = null) =>
        RelaySendAsync(client, HttpMethod.Get, path, secret, userId, tier);

    /// <summary>Sends any method to a secured path over the trusted-relay path (secret + forwarded id).</summary>
    private static async Task<HttpResponseMessage> RelayJsonAsync(
        HttpClient client, string path, string secret, string userId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Relay-Secret", secret);
        request.Headers.Add("X-Relay-User", userId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> RelaySendAsync(
        HttpClient client, HttpMethod method, string path, string secret, string userId, string? tier = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Relay-Secret", secret);
        request.Headers.Add("X-Relay-User", userId);
        // Omitted entirely when null — that IS the case a review test needs to cover (a relay that does
        // not speak the header must not be granted the surface by omission).
        if (tier is not null)
            request.Headers.Add("X-Relay-Tier", tier);
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
    public long AppendTurn(Llm.Models.ConversationTurnRecord turn) => 0;
    public void AddCheckpoint(string conversationId, string summary) { }
    public void SoftDelete(string conversationId) => DeletedKey = conversationId;

    /// <summary>What the feedback endpoint asked for, so a test can assert the key it composed.</summary>
    public (string ConversationId, long TurnId, Llm.Models.TurnFeedbackRating? Rating, string? Note)? Feedback { get; private set; }

    /// <summary>Set false to make the store report the turn as not belonging to the named conversation.</summary>
    public bool FeedbackAccepted { get; set; } = true;

    public bool SetTurnFeedback(string conversationId, long turnId, Llm.Models.TurnFeedbackRating? rating, string? note)
    {
        Feedback = (conversationId, turnId, rating, note);
        return FeedbackAccepted;
    }

    /// <summary>The switches each conversation carries, so a test can stand one up and assert the turn read it.</summary>
    public Dictionary<string, Llm.Models.ConversationPreferences> Preferences { get; } = new(StringComparer.Ordinal);

    /// <summary>Every conversation <c>/new</c> asked to create, in order.</summary>
    public List<string> Created { get; } = new();

    public Llm.Models.ConversationPreferences GetPreferences(string conversationId) =>
        Preferences.TryGetValue(conversationId, out var p) ? p : Llm.Models.ConversationPreferences.Unset;

    public void SetPreferences(string conversationId, Llm.Models.ConversationPreferences delta)
    {
        // Mirror the real store's per-field latest-wins: a null field says nothing about that switch.
        var standing = GetPreferences(conversationId);
        Preferences[conversationId] = new Llm.Models.ConversationPreferences(
            delta.Think ?? standing.Think,
            delta.Autorun ?? standing.Autorun);
    }

    public bool CreateConversation(string conversationId)
    {
        if (Created.Contains(conversationId))
            return false;
        Created.Add(conversationId);
        return true;
    }

    /// <summary>What <c>GetStats</c> hands back, and the surface it was asked for.</summary>
    public Llm.Models.ConversationStats Stats { get; set; } = EmptyStats;
    public string? StatsSurface { get; private set; }

    public Llm.Models.ConversationStats GetStats(string surfacePrefix)
    {
        StatsSurface = surfacePrefix;
        return Stats;
    }

    internal static Llm.Models.ConversationStats EmptyStats => new()
    {
        Conversations = 0, DeletedConversations = 0, Actors = 0, Turns = 0,
        OkTurns = 0, ErrorTurns = 0, CapHitTurns = 0, CancelledTurns = 0, UnrecordedOutcomeTurns = 0,
        ThinkingTurns = 0, TurnsWithoutTool = 0, ToolCalls = 0,
        Tools = Array.Empty<Llm.Models.ToolStat>(),
        PromptVersions = Array.Empty<Llm.Models.PromptVersionStat>(),
        Activity = Array.Empty<Llm.Models.DailyTurnCount>(),
        // No votes means no satisfaction rate — null, not 0%, which would read as "every answer failed".
        RatedTurns = 0, PositiveTurns = 0, NegativeTurns = 0, SatisfactionPercent = null,
        FeedbackNotes = Array.Empty<Llm.Models.FeedbackNote>(),
    };
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
