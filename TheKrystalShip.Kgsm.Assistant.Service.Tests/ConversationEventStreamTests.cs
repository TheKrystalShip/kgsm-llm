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

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// <c>GET /events</c> — a person's own conversation changes, pushed to their own open surfaces so a
/// chat held in two places agrees with itself. What these pin is the part that cannot be recovered by
/// re-reading later: that a change reaches the caller's OTHER stream, that it reaches nobody else's,
/// and that a surface can recognise its own echo rather than re-applying what it just did.
/// </summary>
public class ConversationEventStreamTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConversationEventStreamTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly string DatabasePath = Path.Combine(
        Path.GetTempPath(), "kgsm-conv-events-" + Guid.NewGuid().ToString("N"), "conversations.db");

    private const string AdminRole = "role-admin";

    /// <summary>A host where every named user is an admin, so no test is about the gate.</summary>
    private WebApplicationFactory<Program> Factory(params string[] users)
    {
        var discord = Substitute.For<IDiscordDirectory>();
        foreach (var user in users)
            discord.GetGuildRolesAsync(user, Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<string>?>([AdminRole]);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            builder.UseSetting("Auth:SigningKey", "conversation-events-signing-key");
            builder.UseSetting("Auth:HostId", "test-host");
            builder.UseSetting("Conversation:DatabasePath", DatabasePath);
            builder.UseSetting("Assistant:ActionsEnabled", "true");
            builder.UseSetting("KgsmAuth:RoleAdminIds", AdminRole);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDiscordDirectory>();
                services.AddSingleton(discord);
            });
        });
    }

    private static async Task<HttpClient> AuthedAsync(WebApplicationFactory<Program> factory, string user)
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new DiscordIdentity(user, user, user, null, ["identify"]);
        var sessionId = "sid_" + Guid.NewGuid().ToString("N");
        var access = tokens.MintAccess(identity, KgsmTier.Admin, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, user, "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "tests", "jti_seed"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        return client;
    }

    /// <summary>One frame off the wire: the SSE event name and its parsed payload.</summary>
    private sealed record Frame(string Name, JsonElement Data);

    /// <summary>
    /// An open <c>/events</c> connection, read frame by frame. Heartbeat comments are skipped — they
    /// keep the socket warm and say nothing.
    /// </summary>
    private sealed class Stream : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;
        private readonly CancellationTokenSource _cts;

        private Stream(HttpResponseMessage response, StreamReader reader, CancellationTokenSource cts)
            => (_response, _reader, _cts) = (response, reader, cts);

        public static async Task<Stream> OpenAsync(HttpClient client)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = new HttpRequestMessage(HttpMethod.Get, "/events");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            return new Stream(response, reader, cts);
        }

        /// <summary>The next real frame, or null if the stream ended or the test's patience ran out.</summary>
        public async Task<Frame?> NextAsync()
        {
            string? name = null;
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null)
                    return null;
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    return new Frame(name ?? "", JsonDocument.Parse(line[6..]).RootElement);
            }
            return null;
        }

        /// <summary>The next frame that is not the opening hello.</summary>
        public async Task<Frame?> NextChangeAsync()
        {
            for (var i = 0; i < 8; i++)
            {
                var frame = await NextAsync();
                if (frame is null) return null;
                if (frame.Name != "hello") return frame;
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _reader.Dispose();
            _response.Dispose();
            _cts.Dispose();
        }
    }

    private static async Task<Frame> HelloAsync(Stream stream)
    {
        var hello = await stream.NextAsync();
        hello.Should().NotBeNull("the stream names itself before anything else");
        hello!.Name.Should().Be("hello");
        return hello;
    }

    [Fact]
    public async Task TheStreamRequiresABearer()
    {
        var response = await Factory().CreateClient().GetAsync("/events");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ASwitchFlippedOnOneSurface_ReachesTheSamePersonsOther()
    {
        // The whole point: two surfaces on one conversation, and neither polling the other.
        var factory = Factory("watcher-user");
        var watching = await AuthedAsync(factory, "watcher-user");
        var acting = await AuthedAsync(factory, "watcher-user");

        await using var stream = await Stream.OpenAsync(watching);
        await HelloAsync(stream);

        await acting.PostAsJsonAsync("/commands/think", new CommandRequest("watched-chat", ChatCommands.On));

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Name.Should().Be("conversation.switches");
        frame.Data.GetProperty("conversationId").GetString().Should().Be("watched-chat");
        frame.Data.GetProperty("think").GetBoolean().Should().BeTrue();

        // The frame states where BOTH switches now stand, resolved as the listing resolves them — so
        // applying it lands a surface exactly where a re-read would.
        frame.Data.GetProperty("autorun").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AChangeNeverReachesAnotherPersonsStream()
    {
        // Conversations are namespaced per user, and so is this: a stream carries its own caller's
        // changes and no one else's, whatever ids happen to collide.
        var factory = Factory("someone", "somebody-else");
        var watching = await AuthedAsync(factory, "someone");
        var acting = await AuthedAsync(factory, "somebody-else");

        await using var stream = await Stream.OpenAsync(watching);
        await HelloAsync(stream);

        await acting.PostAsJsonAsync("/commands/think", new CommandRequest("shared-id", ChatCommands.On));
        // Their own change, so this one must arrive and the foreign one must not.
        await watching.PostAsJsonAsync("/commands/autorun", new CommandRequest("mine", ChatCommands.On));

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Data.GetProperty("conversationId").GetString().Should().Be(
            "mine", "the other person's change must not have been delivered first — or at all");
    }

    [Fact]
    public async Task AnEventCarriesTheStreamThatCausedIt_SoASurfaceSkipsItsOwnEcho()
    {
        var factory = Factory("origin-user");
        var client = await AuthedAsync(factory, "origin-user");

        await using var stream = await Stream.OpenAsync(client);
        var hello = await HelloAsync(stream);
        var streamId = hello.Data.GetProperty("streamId").GetString();
        streamId.Should().NotBeNullOrEmpty();

        var request = new HttpRequestMessage(HttpMethod.Post, "/commands/think")
        {
            Content = JsonContent.Create(new CommandRequest("echo-chat", ChatCommands.On)),
        };
        request.Headers.Add("X-Assistant-Origin", streamId);
        await client.SendAsync(request);

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Data.GetProperty("origin").GetString().Should().Be(
            streamId, "a surface must be able to tell its own change from one made elsewhere");
    }

    [Fact]
    public async Task AChangeWithNoNamedOrigin_CarriesNone()
    {
        // Nothing is invented for a caller that did not name a stream — the relay path is exactly that,
        // and every surface then applies the frame.
        var factory = Factory("plain-user");
        var client = await AuthedAsync(factory, "plain-user");

        await using var stream = await Stream.OpenAsync(client);
        await HelloAsync(stream);

        await client.PostAsJsonAsync("/commands/autorun", new CommandRequest("plain-chat", ChatCommands.On));

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Data.GetProperty("origin").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task StartingAConversation_AnnouncesIt()
    {
        var factory = Factory("starter-user");
        var client = await AuthedAsync(factory, "starter-user");

        await using var stream = await Stream.OpenAsync(client);
        await HelloAsync(stream);

        await client.PostAsJsonAsync("/commands/new", new CommandRequest("announced-chat"));

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Name.Should().Be("conversation.started");
        frame.Data.GetProperty("conversationId").GetString().Should().Be("announced-chat");
    }

    [Fact]
    public async Task DeletingAConversation_AnnouncesIt()
    {
        var factory = Factory("deleter-user");
        var client = await AuthedAsync(factory, "deleter-user");

        await using var stream = await Stream.OpenAsync(client);
        await HelloAsync(stream);

        await client.DeleteAsync("/conversations/doomed-chat");

        var frame = await stream.NextChangeAsync();
        frame.Should().NotBeNull();
        frame!.Name.Should().Be("conversation.deleted");
        frame.Data.GetProperty("conversationId").GetString().Should().Be("doomed-chat");
    }

    [Fact]
    public async Task TwoStreamsForOnePerson_BothGetIt()
    {
        // The panel and the installed app at once, which is the case the channel exists for.
        var factory = Factory("two-surface-user");
        var client = await AuthedAsync(factory, "two-surface-user");

        await using var panel = await Stream.OpenAsync(client);
        await HelloAsync(panel);
        await using var app = await Stream.OpenAsync(client);
        await HelloAsync(app);

        await client.PostAsJsonAsync("/commands/think", new CommandRequest("both-chat", ChatCommands.On));

        (await panel.NextChangeAsync())!.Data.GetProperty("conversationId").GetString().Should().Be("both-chat");
        (await app.NextChangeAsync())!.Data.GetProperty("conversationId").GetString().Should().Be("both-chat");
    }
}
