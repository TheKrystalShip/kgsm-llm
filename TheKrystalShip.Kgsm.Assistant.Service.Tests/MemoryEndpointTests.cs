using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;
using static TheKrystalShip.Kgsm.Assistant.Service.Tests.AuthStubs;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The memory surface a person reads and prunes for themselves: <c>GET /memories</c>,
/// <c>DELETE /memories/{key}</c> and the <c>/memory</c> command. The claim these hold is that the
/// owner is derived server-side, so what a caller sees and what they can drop is only ever their own.
/// </summary>
public class MemoryEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MemoryEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly string DatabasePath = Path.Combine(
        Path.GetTempPath(), "kgsm-memories-" + Guid.NewGuid().ToString("N"), "conversations.db");

    private WebApplicationFactory<Program> Factory(KgsmTier tier = KgsmTier.Viewer)
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, tier);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            builder.UseSetting("Auth:SigningKey", "memory-endpoint-signing-key");
            // ⚠ Never the default — that is the host's real shared account store, and opening it
            // creates it.
            builder.UseSetting("Auth:UsersDbPath",
                Path.Combine(Path.GetTempPath(), $"kgsm-assistant-tests-users-{Guid.NewGuid():N}.db"));
            builder.UseSetting("Auth:HostId", "test-host");
            builder.UseSetting("Conversation:DatabasePath", DatabasePath);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISignInService>();
                services.RemoveAll<IAuthorityProvider>();
                services.AddSingleton(discord);
                services.AddSingleton((IAuthorityProvider)discord);
            });
        });
    }

    /// <summary>A client signed in as <paramref name="userId"/>, so two callers can be told apart.</summary>
    private static async Task<HttpClient> AuthedAsync(
        WebApplicationFactory<Program> factory, string userId = "user1")
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new KgsmIdentity(
            KgsmActorProvider.Discord, userId, userId, "User " + userId, null, ["identify"]);
        var sessionId = "sid_" + Guid.NewGuid().ToString("N");
        var access = tokens.MintAccess(identity, KgsmTier.Viewer, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, userId, "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "tests", "jti_seed"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        return client;
    }

    /// <summary>Writes straight into the store, so a test can set up what is remembered without a
    /// model turn.</summary>
    private static void Remember(
        WebApplicationFactory<Program> factory, string owner, string key, string summary,
        string body = "")
    {
        factory.Services.GetRequiredService<IMemoryStore>()
            .Write(owner, new MemoryRecord(key, summary, body, DateTimeOffset.UtcNow, owner));
    }

    [Fact]
    public async Task Memories_RequireABearer()
    {
        var response = await Factory().CreateClient().GetAsync("/memories");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Memories_ListsTheCallersOwn()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "listing-user");
        Remember(factory, "web:listing-user", "preferred-game", "Tests with Factorio.", "Boots fast.");

        var memories = await client.GetFromJsonAsync<MemoryDto[]>("/memories");

        memories.Should().ContainSingle();
        memories![0].Key.Should().Be("preferred-game");
        memories[0].Summary.Should().Be("Tests with Factorio.");
        memories[0].Body.Should().Be("Boots fast.");
        memories[0].WrittenAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Memories_NeverShowSomebodyElses()
    {
        // The owner comes off the verified principal, so there is no id a caller could send to reach
        // another person's memory — the listing simply has nothing of theirs in it.
        var factory = Factory();
        Remember(factory, "web:other-person", "secret", "Something private.");

        var client = await AuthedAsync(factory, "nosy-user");
        var memories = await client.GetFromJsonAsync<MemoryDto[]>("/memories");

        memories.Should().NotBeNull();
        memories!.Should().NotContain(m => m.Summary == "Something private.");
    }

    [Fact]
    public async Task Memories_NothingRemembered_IsAnEmptyList()
    {
        var client = await AuthedAsync(Factory(), "brand-new-user");
        var memories = await client.GetFromJsonAsync<MemoryDto[]>("/memories");

        memories.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task Delete_DropsIt()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "pruning-user");
        Remember(factory, "web:pruning-user", "stale-note", "No longer true.");

        var response = await client.DeleteAsync("/memories/stale-note");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<MemoryDto[]>("/memories"))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        // Forgetting what is already forgotten is the state the caller asked for, not an error.
        var client = await AuthedAsync(Factory(), "idempotent-user");

        (await client.DeleteAsync("/memories/never-existed")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync("/memories/never-existed")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_CannotReachAnotherPersonsMemory()
    {
        var factory = Factory();
        Remember(factory, "web:victim", "keep-this", "Still true.");

        var client = await AuthedAsync(factory, "attacker");
        var response = await client.DeleteAsync("/memories/keep-this");

        // It answers 204 — the ATTACKER has no such memory, and that is the honest outcome. What
        // matters is that the victim's is untouched: the key never addressed it.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Services.GetRequiredService<IMemoryStore>()
            .Get("web:victim", "keep-this").Should().NotBeNull();
    }

    [Fact]
    public async Task MemoryCommand_ListsWhatIsRemembered()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "command-user");
        Remember(factory, "web:command-user", "preferred-game", "Tests with Factorio.");

        var response = await client.PostAsJsonAsync(
            "/commands/memory", new CommandRequest(null, null));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("memories").EnumerateArray()
            .Select(m => m.GetProperty("key").GetString())
            .Should().Contain("preferred-game");
    }

    [Fact]
    public async Task MemoryCommand_SaysSoWhenNothingIsRemembered()
    {
        var client = await AuthedAsync(Factory(), "quiet-user");

        var response = await client.PostAsJsonAsync(
            "/commands/memory", new CommandRequest(null, null));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("message").GetString().Should().Contain("haven't written anything down");
        body.GetProperty("memories").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task MemoryCommand_IsOfferedToAViewer()
    {
        // Reading what is remembered about you needs no authority over any server.
        var client = await AuthedAsync(Factory(), "viewer-user");
        var commands = await client.GetFromJsonAsync<CommandDto[]>("/commands");

        commands!.Select(c => c.Name).Should().Contain("memory");
    }
}
