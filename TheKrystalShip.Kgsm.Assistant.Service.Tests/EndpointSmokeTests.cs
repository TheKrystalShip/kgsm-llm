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

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant;
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
        Action<IWebHostBuilder>? configure = null) =>
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
        assistant.RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssistantResult.Ok("hello from assistant", Array.Empty<PendingConfirmation>())));

        var client = Authed(Factory(assistant));
        var response = await client.PostAsJsonAsync("/turn", new { prompt = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TurnResponse>();
        body!.Text.Should().Be("hello from assistant");
        body.Confirmations.Should().BeEmpty();
        await assistant.Received(1).RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>());
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
        assistant.RunStreamAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
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
        assistant.RunStreamAsync("web:user1", "status?", Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
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
        body.Should().Contain("event: done");
    }

    [Fact]
    public async Task Turn_StreamAccept_ConfirmationEventCarriesTokenBoundToCaller()
    {
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
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
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
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
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), null, Arg.Any<CancellationToken>())
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
}
