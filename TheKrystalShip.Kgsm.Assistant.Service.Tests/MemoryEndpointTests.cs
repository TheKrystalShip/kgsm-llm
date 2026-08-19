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

    // ---- writing one by hand -----------------------------------------------------------------

    [Fact]
    public async Task Write_RequiresABearer()
    {
        var response = await Factory().CreateClient()
            .PutAsJsonAsync("/memories/anything", new MemoryWriteRequest("Something."));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Write_StoresItUnderTheCallersOwnOwner()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "writing-user");

        var response = await client.PutAsJsonAsync(
            "/memories/preferred-game", new MemoryWriteRequest("Tests with Factorio.", "Boots fast."));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var written = await response.Content.ReadFromJsonAsync<MemoryDto>();
        written!.Key.Should().Be("preferred-game");
        written.Summary.Should().Be("Tests with Factorio.");
        written.Body.Should().Be("Boots fast.");

        var listed = await client.GetFromJsonAsync<MemoryDto[]>("/memories");
        listed.Should().ContainSingle().Which.Summary.Should().Be("Tests with Factorio.");

        // Under the caller's own owner and no other — the same claim the read and delete paths hold.
        factory.Services.GetRequiredService<IMemoryStore>()
            .Get("web:writing-user", "preferred-game").Should().NotBeNull();
    }

    [Fact]
    public async Task Write_ByHand_IsSourcedToThePerson()
    {
        // Origin null is what the record reserves for a memory somebody entered themselves, and it is
        // the only thing that lets a surface say why a memory exists.
        var factory = Factory();
        var client = await AuthedAsync(factory, "sourcing-user");
        Remember(factory, "web:sourcing-user", "learned-note", "The assistant worked this out.");

        await client.PutAsJsonAsync("/memories/typed-note", new MemoryWriteRequest("I typed this."));

        var listed = (await client.GetFromJsonAsync<MemoryDto[]>("/memories"))!;
        listed.Single(m => m.Key == "typed-note").Source.Should().Be("you");
        listed.Single(m => m.Key == "learned-note").Source.Should().Be("conversation");
    }

    [Fact]
    public async Task Write_SameKeyAgain_SupersedesRatherThanDuplicating()
    {
        var client = await AuthedAsync(Factory(), "correcting-user");

        await client.PutAsJsonAsync("/memories/note", new MemoryWriteRequest("First reading."));
        await client.PutAsJsonAsync("/memories/note", new MemoryWriteRequest("Corrected reading."));

        var listed = await client.GetFromJsonAsync<MemoryDto[]>("/memories");
        listed.Should().ContainSingle().Which.Summary.Should().Be("Corrected reading.");
    }

    [Fact]
    public async Task Write_CorrectingOneTheAssistantWrote_MakesItThePersons()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "reclaiming-user");
        Remember(factory, "web:reclaiming-user", "note", "What the chat concluded.");

        await client.PutAsJsonAsync("/memories/note", new MemoryWriteRequest("What I actually meant."));

        var listed = await client.GetFromJsonAsync<MemoryDto[]>("/memories");
        listed.Should().ContainSingle().Which.Source.Should().Be("you");
    }

    [Fact]
    public async Task Write_KeyIsSanitizedTheSameWayTheListingShowsIt()
    {
        // A key read out of a listing must address the row it was shown, and a key a person typed in
        // their own spelling must land on the same one — or a correction files a near-duplicate.
        var client = await AuthedAsync(Factory(), "spelling-user");

        await client.PutAsJsonAsync("/memories/Preferred Game!", new MemoryWriteRequest("First."));
        await client.PutAsJsonAsync("/memories/preferred-game", new MemoryWriteRequest("Second."));

        var listed = await client.GetFromJsonAsync<MemoryDto[]>("/memories");
        listed.Should().ContainSingle().Which.Key.Should().Be("preferred-game");
        listed![0].Summary.Should().Be("Second.");
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("---")]
    public async Task Write_KeyThatNamesNothing_IsRefused(string key)
    {
        var client = await AuthedAsync(Factory(), "unnameable-user");

        var response = await client.PutAsJsonAsync(
            "/memories/" + Uri.EscapeDataString(key), new MemoryWriteRequest("Something."));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Write_WithoutASummary_IsRefused(string? summary)
    {
        // The summary is the line every later turn reads. A memory without one is a note that says
        // nothing, injected into every prompt.
        var client = await AuthedAsync(Factory(), "blank-user");

        var response = await client.PutAsJsonAsync(
            "/memories/blank", new MemoryWriteRequest(summary));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<MemoryDto[]>("/memories"))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_OverlongSummary_IsRefusedNamingTheLimit()
    {
        var factory = Factory();
        var client = await AuthedAsync(factory, "verbose-user");
        var limits = await client.GetFromJsonAsync<MemoryLimitsDto>("/memories/limits");

        var response = await client.PutAsJsonAsync(
            "/memories/long", new MemoryWriteRequest(new string('a', limits!.MaxSummaryLength + 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain(limits.MaxSummaryLength.ToString());
    }

    [Fact]
    public async Task Write_OverlongBody_IsRefusedNamingTheLimit()
    {
        var client = await AuthedAsync(Factory(), "essay-user");
        var limits = await client.GetFromJsonAsync<MemoryLimitsDto>("/memories/limits");

        var response = await client.PutAsJsonAsync("/memories/long-body",
            new MemoryWriteRequest("Fine.", new string('a', limits!.MaxBodyLength + 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain(limits.MaxBodyLength.ToString());
    }

    [Fact]
    public async Task Write_AtTheCap_RefusesANewOneAndStillAcceptsACorrection()
    {
        // The two halves of the cap are one test because they are one rule: the count is what is
        // capped, and a rewrite adds nothing to it. Refusing the correction would leave somebody full
        // and unable to fix a memory that is wrong.
        var factory = Factory();
        var client = await AuthedAsync(factory, "full-user");
        var limits = (await client.GetFromJsonAsync<MemoryLimitsDto>("/memories/limits"))!;

        for (var i = 0; i < limits.MaxPerOwner; i++)
            Remember(factory, "web:full-user", "note-" + i, "Note " + i);

        var overflow = await client.PutAsJsonAsync(
            "/memories/one-too-many", new MemoryWriteRequest("Would be the next one."));
        overflow.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var refusal = await overflow.Content.ReadFromJsonAsync<JsonElement>();
        refusal.GetProperty("error").GetString().Should().Contain(limits.MaxPerOwner.ToString());

        var correction = await client.PutAsJsonAsync(
            "/memories/note-0", new MemoryWriteRequest("Corrected at the cap."));
        correction.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Write_CannotReachAnotherPersonsMemory()
    {
        var factory = Factory();
        Remember(factory, "web:write-victim", "keep-this", "Still true.");

        var client = await AuthedAsync(factory, "write-attacker");
        var response = await client.PutAsJsonAsync(
            "/memories/keep-this", new MemoryWriteRequest("Overwritten."));

        // It succeeds — for the ATTACKER, who now has a memory of their own by that name. The victim's
        // is untouched, because the key never addressed it.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IMemoryStore>()
            .Get("web:write-victim", "keep-this")!.Summary.Should().Be("Still true.");
    }

    [Fact]
    public async Task Limits_AreReadableByAViewer()
    {
        // An editor reads its counters from here, so this is as viewer-gated as the listing it sits
        // beside — and it describes the host, never the caller.
        var client = await AuthedAsync(Factory(), "limits-user");

        var limits = await client.GetFromJsonAsync<MemoryLimitsDto>("/memories/limits");

        limits!.MaxPerOwner.Should().BePositive();
        limits.MaxSummaryLength.Should().BePositive();
        limits.MaxBodyLength.Should().BeGreaterThan(limits.MaxSummaryLength);
    }
}
