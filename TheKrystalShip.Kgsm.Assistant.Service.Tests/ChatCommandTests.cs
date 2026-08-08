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
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The typed-command surface: the catalog the leaf publishes, the tier it filters that catalog by,
/// and the fact that every command it lists has a body behind it. The last one is the load-bearing
/// claim — surfaces treat <c>GET /commands</c> as authoritative rather than advisory, which is only
/// true while every listed command actually runs.
/// </summary>
public class ChatCommandTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChatCommandTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly string DatabasePath = Path.Combine(
        Path.GetTempPath(), "kgsm-chat-commands-" + Guid.NewGuid().ToString("N"), "conversations.db");

    private const string OperatorRole = "role-op";
    private const string AdminRole = "role-admin";

    /// <summary>
    /// A host where <paramref name="roles"/> decide the caller's tier, so a test names the authority it
    /// wants rather than asserting against whatever the ambient config happens to grant.
    /// </summary>
    private WebApplicationFactory<Program> Factory(params string[] roles) => Factory(null, roles);

    private WebApplicationFactory<Program> Factory(IServerAssistant? assistant, params string[] roles)
    {
        var discord = Substitute.For<IDiscordDirectory>();
        discord.GetGuildRolesAsync("user1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(roles);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            builder.UseSetting("Auth:SigningKey", "chat-command-signing-key");
            builder.UseSetting("Auth:HostId", "test-host");
            builder.UseSetting("Conversation:DatabasePath", DatabasePath);
            builder.UseSetting("Assistant:ActionsEnabled", "true");
            builder.UseSetting("KgsmAuth:RoleOperatorIds", OperatorRole);
            builder.UseSetting("KgsmAuth:RoleAdminIds", AdminRole);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDiscordDirectory>();
                services.AddSingleton(discord);
                if (assistant is not null)
                    services.AddSingleton(assistant);
            });
        });
    }

    private static async Task<HttpClient> AuthedAsync(WebApplicationFactory<Program> factory)
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new DiscordIdentity("user1", "user1", "User One", null, ["identify"]);
        var sessionId = "sid_" + Guid.NewGuid().ToString("N");
        var access = tokens.MintAccess(identity, KgsmTier.Viewer, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, "user1", "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "tests", "jti_seed"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        return client;
    }

    private static async Task<JsonElement> RunAsync(
        HttpClient client, string command, string? conversationId = null, string? argument = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/commands/{command}", new CommandRequest(conversationId, argument));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"/{command} should run");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Commands_RequireABearer()
    {
        var response = await Factory().CreateClient().GetAsync("/commands");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Commands_ListsTheViewerCatalog_ToAViewer()
    {
        var client = await AuthedAsync(Factory());
        var commands = await client.GetFromJsonAsync<CommandDto[]>("/commands");

        commands.Should().NotBeNull();
        commands!.Select(c => c.Name).Should().BeEquivalentTo(
            ChatCommands.For(KgsmTier.Viewer).Select(c => c.Name));
    }

    [Fact]
    public async Task Commands_OmitsAdminCommands_FromANonAdmin()
    {
        // The gate is what keeps /autorun off an operator's list. A listed-then-refused command would
        // be the surface offering something it then rejects, which is the thing the filter prevents.
        var client = await AuthedAsync(Factory(OperatorRole));
        var commands = await client.GetFromJsonAsync<CommandDto[]>("/commands");

        commands!.Select(c => c.Name).Should().NotContain("autorun");
    }

    [Fact]
    public async Task Commands_IncludesAutorun_ForAnAdmin()
    {
        var client = await AuthedAsync(Factory(AdminRole));
        var commands = await client.GetFromJsonAsync<CommandDto[]>("/commands");

        commands.Should().NotBeNull();
        commands!.Select(c => c.Name).Should().Contain("autorun");
        commands!.Single(c => c.Name == "autorun").Options
            .Single().Values.Should().BeEquivalentTo([ChatCommands.On, ChatCommands.Off]);
    }

    [Fact]
    public async Task Running_AnAdminCommand_AsAnOperator_IsForbidden()
    {
        // Re-checked at the POST rather than trusted from the listing: a client can post any name, and
        // the listing is a convenience, never the authorization.
        var client = await AuthedAsync(Factory(OperatorRole));
        var response = await client.PostAsJsonAsync("/commands/autorun", new CommandRequest("c1", "on"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Running_AnUnknownCommand_IsNotFound()
    {
        var client = await AuthedAsync(Factory());
        var response = await client.PostAsJsonAsync("/commands/nope", new CommandRequest("c1"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EveryListedCommand_HasABody()
    {
        // The catalog's whole claim to a client is that listing means runnable. A command added to
        // ChatCommands without a case in the endpoint answers 501, and this is what says so.
        var client = await AuthedAsync(Factory(AdminRole));
        var commands = await client.GetFromJsonAsync<CommandDto[]>("/commands");

        foreach (var command in commands!)
        {
            var response = await client.PostAsJsonAsync(
                $"/commands/{command.Name}", new CommandRequest("probe-" + command.Name));

            response.StatusCode.Should().NotBe(
                HttpStatusCode.NotImplemented,
                $"/{command.Name} is listed, so it must have a body behind it");
        }
    }

    [Fact]
    public async Task Think_TogglesWhenGivenNoArgument_AndStatesWhenGivenOne()
    {
        var client = await AuthedAsync(Factory());

        var on = await RunAsync(client, "think", "toggle-chat", ChatCommands.On);
        on.GetProperty("state").GetBoolean().Should().BeTrue();

        // No argument flips what stands, which is what the composer's button and the CLI already do.
        var toggled = await RunAsync(client, "think", "toggle-chat");
        toggled.GetProperty("state").GetBoolean().Should().BeFalse();

        var off = await RunAsync(client, "think", "toggle-chat", ChatCommands.Off);
        off.GetProperty("state").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Think_IsScopedToOneConversation()
    {
        // The switches live on the conversation, not on the person — turning one on here must not
        // reach a chat they hold somewhere else.
        var client = await AuthedAsync(Factory());

        await RunAsync(client, "think", "chat-a", ChatCommands.On);
        var other = await RunAsync(client, "think", "chat-b", ChatCommands.On);

        // chat-b was unset, so naming "on" leaves it on rather than toggling a value chat-a set.
        other.GetProperty("state").GetBoolean().Should().BeTrue();

        var backOnA = await RunAsync(client, "think", "chat-a");
        backOnA.GetProperty("state").GetBoolean().Should().BeFalse("chat-a's own value is what toggles");
    }

    [Fact]
    public async Task ABadSwitchArgument_IsRefused_RatherThanGuessed()
    {
        var client = await AuthedAsync(Factory());
        var response = await client.PostAsJsonAsync(
            "/commands/think", new CommandRequest("chat-c", "yes"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task New_CreatesTheConversation_AndIsIdempotent()
    {
        var client = await AuthedAsync(Factory());

        var first = await RunAsync(client, "new", "fresh-chat");
        first.GetProperty("conversationId").GetString().Should().Be("fresh-chat");

        var again = await RunAsync(client, "new", "fresh-chat");
        again.GetProperty("conversationId").GetString().Should().Be("fresh-chat");

        // It exists server-side the moment it is started, before anything is said in it — which is what
        // lets another device see it.
        var listed = await client.GetFromJsonAsync<JsonElement>("/conversations");
        listed.EnumerateArray().Select(c => c.GetProperty("id").GetString())
            .Should().Contain("fresh-chat");
    }

    [Fact]
    public async Task New_NeedsAConversationToStart()
    {
        // The bare per-user conversation always exists; "start a fresh one" with no id names nothing.
        var client = await AuthedAsync(Factory());
        var response = await client.PostAsJsonAsync("/commands/new", new CommandRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Help_AnswersTheSameCatalogTheListingDoes()
    {
        var client = await AuthedAsync(Factory(AdminRole));

        var listed = await client.GetFromJsonAsync<CommandDto[]>("/commands");
        var help = await RunAsync(client, "help", "help-chat");

        help.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().BeEquivalentTo(listed!.Select(c => c.Name));
    }

    [Fact]
    public async Task Tools_AnswersTheSameToolsTheToolsEndpointDoes()
    {
        var client = await AuthedAsync(Factory());

        var listed = await client.GetFromJsonAsync<JsonElement>("/tools");
        var tools = await RunAsync(client, "tools", "tools-chat");

        tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().BeEquivalentTo(
                listed.EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    /// <summary>
    /// A turn against <paramref name="conversationId"/>, answering what the assistant was asked to do
    /// with it: whether to think, and whether to run actions without confirmation.
    /// </summary>
    private static async Task<(bool Think, bool AutoExecute)> TurnFlagsAsync(
        IServerAssistant assistant, HttpClient client, string conversationId)
    {
        bool think = false, autoExecute = false;
        assistant.RunAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(call =>
            {
                think = call.ArgAt<bool>(3);
                autoExecute = call.ArgAt<bool>(4);
                return Task.FromResult(AssistantResult.Ok("ok", Array.Empty<PendingConfirmation>()));
            });

        var response = await client.PostAsJsonAsync(
            "/turn", new { prompt = "hi", conversationId });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (think, autoExecute);
    }

    [Fact]
    public async Task ATurn_ReadsThinking_FromTheConversation()
    {
        // The switch is not a turn field. The leaf owns it, so the turn reads what the conversation
        // carries rather than what a client asked for.
        var assistant = Substitute.For<IServerAssistant>();
        var client = await AuthedAsync(Factory(assistant, AdminRole));

        var before = await TurnFlagsAsync(assistant, client, "think-turn");
        before.Think.Should().BeFalse("nothing has set it, so it falls to the configured default");

        await RunAsync(client, "think", "think-turn", ChatCommands.On);

        var after = await TurnFlagsAsync(assistant, client, "think-turn");
        after.Think.Should().BeTrue();
    }

    [Fact]
    public async Task ATurn_ReadsAutoRun_FromTheConversation()
    {
        var assistant = Substitute.For<IServerAssistant>();
        var client = await AuthedAsync(Factory(assistant, AdminRole));

        var before = await TurnFlagsAsync(assistant, client, "auto-turn");
        before.AutoExecute.Should().BeFalse();

        await RunAsync(client, "autorun", "auto-turn", ChatCommands.On);

        var after = await TurnFlagsAsync(assistant, client, "auto-turn");
        after.AutoExecute.Should().BeTrue();
    }

    [Fact]
    public async Task AutoRun_ArmedInOneConversation_DoesNotReachAnother()
    {
        // This is why the switch is per-conversation: it is the one thing that skips the confirmation
        // gate, and arming it here must not silently arm a chat held somewhere else.
        var assistant = Substitute.For<IServerAssistant>();
        var client = await AuthedAsync(Factory(assistant, AdminRole));

        await RunAsync(client, "autorun", "armed-chat", ChatCommands.On);

        var elsewhere = await TurnFlagsAsync(assistant, client, "other-chat");
        elsewhere.AutoExecute.Should().BeFalse();
    }

    [Fact]
    public async Task AutoRun_StoredByAnAdmin_IsStillNarrowedByTierAtTheTurn()
    {
        // Stored intent is not authority. An operator's turn on a conversation carrying autorun=true
        // still stages its actions, because auto-execute ANDs the stored value with the caller's tier.
        var admin = Substitute.For<IServerAssistant>();
        var adminClient = await AuthedAsync(Factory(admin, AdminRole));
        await RunAsync(adminClient, "autorun", "shared-chat", ChatCommands.On);

        var assistant = Substitute.For<IServerAssistant>();
        var operatorClient = await AuthedAsync(Factory(assistant, OperatorRole));

        var flags = await TurnFlagsAsync(assistant, operatorClient, "shared-chat");
        flags.AutoExecute.Should().BeFalse();
    }

    [Fact]
    public void TheShippedManifest_CarriesTheWholeCatalog_KeyedByGate()
    {
        var manifest = CommandManifest.Build();

        manifest.SchemaVersion.Should().Be(2);
        manifest.Leaf.Should().Be(ChatCommands.LeafId);
        manifest.Surface.Should().Be(ChatCommands.Surface);

        // The file documents the leaf, so it is the WHOLE catalog — unlike the endpoint, which is
        // filtered to the caller.
        manifest.Gates.SelectMany(g => g.Value).Select(c => c.Name)
            .Should().BeEquivalentTo(ChatCommands.All.Select(c => c.Name));

        // And each command sits under the gate that admits it, which is what keying by gate buys: a
        // command cannot be added without landing in a bucket.
        foreach (var command in ChatCommands.All)
        {
            manifest.Gates[KgsmTiers.ToWire(command.Gate)]
                .Should().Contain(c => c.Name == command.Name);
        }
    }

    [Fact]
    public void TheShippedManifest_MatchesTheFileTheBuildCommitted()
    {
        // The file is generated by the build and committed. If they disagree, the committed file is
        // stale — the panel would be describing a build that no longer exists.
        var committed = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "deploy", "kgsm-llm.commands.json");
        File.Exists(committed).Should().BeTrue($"the build writes {committed}");

        using var document = JsonDocument.Parse(File.ReadAllText(committed));
        var root = document.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(ChatCommands.SchemaVersion);
        root.GetProperty("leaf").GetString().Should().Be(ChatCommands.LeafId);
        root.GetProperty("surface").GetString().Should().Be(ChatCommands.Surface);
        root.GetProperty("gates").EnumerateObject()
            .SelectMany(g => g.Value.EnumerateArray())
            .Select(c => c.GetProperty("name").GetString())
            .Should().BeEquivalentTo(ChatCommands.All.Select(c => c.Name));
    }
}
