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
    private const string AccessToken = "discord-access-token";

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
        var token = store.Create(new Session(userId, "User One", AccessToken, DateTimeOffset.UtcNow.AddHours(1)));
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
        assistant.RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssistantResult.Ok("hello from assistant", Array.Empty<PendingConfirmation>())));

        var client = Authed(Factory(assistant));
        var response = await client.PostAsJsonAsync("/turn", new { prompt = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TurnResponse>();
        body!.Text.Should().Be("hello from assistant");
        body.Confirmations.Should().BeEmpty();
        await assistant.Received(1).RunAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        discord.GetGuildMemberAsync("tok", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = Array.Empty<string>(), User = new DiscordUser { Id = "u1", Username = "Alice" } });

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
        discord.GetGuildMemberAsync(AccessToken, Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = new[] { "role-123" }, User = new DiscordUser { Id = "user1" } });

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
        assistant.RunStreamAsync("web:user1", "hi", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Hel"),
                AssistantStreamEvent.Token("lo"),
                AssistantStreamEvent.Final("Hello")));

        var response = await StreamTurnAsync(Authed(Factory(assistant)), "hi");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: token");
        body.Should().Contain("\"delta\":\"Hel\"");
        body.Should().Contain("event: done");
        body.Should().Contain("\"text\":\"Hello\"");
    }

    [Fact]
    public async Task Turn_StreamAccept_ConfirmationEventCarriesTokenBoundToCaller()
    {
        var assistant = Substitute.For<IServerAssistant>();
        assistant.RunStreamAsync("web:user1", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsyncSeq(
                AssistantStreamEvent.Token("Staging…"),
                AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria")),
                AssistantStreamEvent.Final("Staged.")));

        var factory = Factory(assistant, configure: b => b.UseSetting("Assistant:Confirmation:Key", "test-key"));
        var response = await StreamTurnAsync(Authed(factory), "remove terraria");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: confirmation");
        var token = ExtractConfirmationToken(body);

        // The token minted into the SSE frame must validate AND be bound to the caller (user1).
        var tokenSvc = factory.Services.GetRequiredService<ConfirmationTokenService>();
        tokenSvc.TryValidate(token, out var confirmation, out var stagedBy).Should().BeTrue();
        stagedBy.Should().Be("user1");
        confirmation.Kind.Should().Be(ConfirmationKind.Uninstall);
        confirmation.Target.Should().Be("terraria");
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
            if (!frame.Contains("event: confirmation"))
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
