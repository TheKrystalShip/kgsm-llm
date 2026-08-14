using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
using static TheKrystalShip.Kgsm.Assistant.Service.Tests.AuthStubs;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// A turn is a session with its own lifetime, and every one of that person's surfaces can attach to it.
/// What these pin is the part that only exists because of that: a second surface seeing a turn it did
/// not start, a late arrival being told the whole state rather than the next delta, one turn at a time
/// per conversation with the rest queued, and a stop that any surface can issue.
/// </summary>
public class SharedTurnTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SharedTurnTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly string DatabasePath = Path.Combine(
        Path.GetTempPath(), "kgsm-shared-turns-" + Guid.NewGuid().ToString("N"), "conversations.db");

    private const string AdminRole = "role-admin";

    /// <summary>
    /// An assistant whose turn is held open until the test lets it finish, so a second surface has
    /// something in flight to attach to. Each turn is handed a gate keyed by its prompt.
    /// </summary>
    private sealed class ScriptedAssistant : IServerAssistant
    {
        public readonly SemaphoreSlim Started = new(0);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;

        /// <summary>The style the last streamed turn was run with — what the handler passed down.</summary>
        public ReplyStyle LastStyle = ReplyStyle.Default;

        // Only the streaming path is exercised: it is the one a surface attaches to, and the buffered
        // path deliberately runs outside the session model.
        public Task<AssistantResult> RunAsync(
            string conversationId, string prompt, bool canPerformActions, bool think, bool autoExecute,
            IReadOnlyList<string>? tools, CancellationToken ct, string? draftYaml,
            string? userDisplay, string? leaf, bool sharedConversation, ReplyStyle style) =>
            throw new NotSupportedException("These tests only stream.");

        public Task<Status.ConfirmOutcome> ConfirmAsync(
            PendingConfirmation confirmation, bool authorized, CancellationToken ct) =>
            throw new NotSupportedException("These tests only stream.");

        public Task<Envelope.ToolResult<Blueprints.BlueprintAuthoringData>> FinalizeBlueprintAsync(
            string draftYaml, string blueprintName, bool authorized, CancellationToken ct) =>
            throw new NotSupportedException("These tests only stream.");

        public async IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
            string conversationId, string prompt, bool canPerformActions, bool think, bool autoExecute,
            IReadOnlyList<string>? tools, [EnumeratorCancellation] CancellationToken ct,
            string? draftYaml, string? userDisplay, string? leaf, bool sharedConversation, ReplyStyle style)
        {
            Interlocked.Increment(ref Runs);
            LastStyle = style;
            yield return AssistantStreamEvent.Token("watching ");
            Started.Release();

            // Held here until the test releases it, or until somebody stops the turn.
            await Release.Task.WaitAsync(ct);

            yield return AssistantStreamEvent.Token("done");
            yield return AssistantStreamEvent.Final("watching done", null, 1);
        }
    }

    private WebApplicationFactory<Program> Factory(IServerAssistant assistant)
    {
        var discord = Substitute.For<ISignInService, IAuthorityProvider>();
        StubTier(discord, KgsmTier.Admin);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("KGSM:SocketPath", "/opt/kgsm/kgsm.sock");
            builder.UseSetting("Auth:SigningKey", "shared-turn-signing-key");
            // ⚠ Never the default. /var/lib/kgsm/auth/users.db is the HOST's real account store,
            // shared with every KGSM service on the box, and opening it CREATES it — so an unpinned
            // test run would hand the operator a live accounts file that nobody made.
            builder.UseSetting("Auth:UsersDbPath",
                Path.Combine(Path.GetTempPath(), $"kgsm-assistant-tests-users-{Guid.NewGuid():N}.db"));
            builder.UseSetting("Auth:HostId", "test-host");
            builder.UseSetting("Conversation:DatabasePath", DatabasePath);
            builder.UseSetting("Assistant:ActionsEnabled", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISignInService>();
                services.RemoveAll<IAuthorityProvider>();
                // Both halves come off the one substitute, so the authority a test stubs is the
                // authority the handler resolves.
                services.AddSingleton(discord);
                services.AddSingleton((IAuthorityProvider)discord);
                services.RemoveAll<IServerAssistant>();
                services.AddSingleton(assistant);
            });
        });
    }

    private static async Task<HttpClient> AuthedAsync(WebApplicationFactory<Program> factory, string user)
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var registry = factory.Services.GetRequiredService<ISessionRegistry>();

        var identity = new KgsmIdentity(KgsmActorProvider.Discord, user, user, user, null, ["identify"]);
        var sessionId = "sid_" + Guid.NewGuid().ToString("N");
        var access = tokens.MintAccess(identity, KgsmTier.Admin, sessionId);
        await registry.CreateAsync(new SessionRegistration(
            sessionId, user, "test-host",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "tests", "jti_seed"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        return client;
    }

    /// <summary>One SSE frame: its event name and its parsed payload.</summary>
    private sealed record Frame(string Name, JsonElement Data);

    /// <summary>An open SSE response, read frame by frame.</summary>
    private sealed class Frames(HttpResponseMessage response, StreamReader reader, CancellationTokenSource cts)
        : IAsyncDisposable
    {
        public static async Task<Frames> OpenAsync(HttpClient client, HttpRequestMessage request)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return new Frames(response, new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token)), cts);
        }

        /// <summary>
        /// The next frame, or null when <paramref name="within"/> passes without one. A bounded wait is
        /// what lets a test assert that something does NOT arrive.
        /// </summary>
        public async Task<Frame?> NextAsync(TimeSpan? within = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            if (within is { } limit)
                deadline.CancelAfter(limit);

            string? name = null;
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(deadline.Token);
                    if (line is null) return null;
                    if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        return new Frame(name ?? "", JsonDocument.Parse(line[6..]).RootElement);
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                return null;   // the bounded wait ran out, which is an answer in itself
            }
            return null;
        }

        /// <summary>The next frame satisfying <paramref name="match"/>, skipping heartbeats and noise.</summary>
        public async Task<Frame?> FirstAsync(Func<Frame, bool> match)
        {
            for (var i = 0; i < 60; i++)
            {
                var frame = await NextAsync(TimeSpan.FromSeconds(10));
                if (frame is null) return null;
                if (match(frame)) return frame;
            }
            return null;
        }

        /// <summary>The next frame with one of <paramref name="names"/>.</summary>
        public Task<Frame?> NextOfAsync(params string[] names) =>
            FirstAsync(f => names.Contains(f.Name));

        /// <summary>The next queue frame carrying <paramref name="count"/> waiting turns.</summary>
        public Task<Frame?> QueueOfAsync(int count) => FirstAsync(f =>
            f.Name == "turn.queue" && f.Data.GetProperty("queued").GetArrayLength() == count);

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            reader.Dispose();
            response.Dispose();
            cts.Dispose();
        }
    }

    private static Task<Frames> TurnAsync(HttpClient client, string chatId, string prompt) =>
        Frames.OpenAsync(client, new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt, conversationId = chatId }),
        });

    /// <summary>An /events stream, attached to a conversation the way a surface attaches to one.</summary>
    private static async Task<Frames> WatchAsync(HttpClient client, string chatId)
    {
        var events = await Frames.OpenAsync(client, new HttpRequestMessage(HttpMethod.Get, "/events"));
        var hello = await events.NextAsync();
        hello!.Name.Should().Be("hello");
        var streamId = hello.Data.GetProperty("streamId").GetString();

        var attach = new HttpRequestMessage(HttpMethod.Post, "/events/attach")
        {
            Content = JsonContent.Create(new { conversationId = chatId }),
        };
        attach.Headers.Add("X-Assistant-Origin", streamId);
        (await client.SendAsync(attach)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return events;
    }

    [Fact]
    public async Task ASecondSurfaceSeesATurnItDidNotStart()
    {
        // The whole point: the turn belongs to the conversation, not to the connection that asked.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var sender = await AuthedAsync(factory, "shared-user");
        var watcher = await AuthedAsync(factory, "shared-user");

        await using var watching = await WatchAsync(watcher, "shared-chat");
        await using var sending = await TurnAsync(sender, "shared-chat", "hello");

        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // The watcher receives the very same frame vocabulary the sender does — not a reduced one.
        var frame = await watching.NextOfAsync("text.delta");
        frame.Should().NotBeNull();
        frame!.Data.GetProperty("text").GetString().Should().Be("watching ");

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AttachingMidTurnStatesEverythingSoFar()
    {
        // A surface that arrives late is owed the screen, not the keystrokes it missed.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var sender = await AuthedAsync(factory, "late-user");
        var latecomer = await AuthedAsync(factory, "late-user");

        await using var sending = await TurnAsync(sender, "late-chat", "hello");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        await using var watching = await WatchAsync(latecomer, "late-chat");

        var attach = await watching.NextOfAsync("turn.attach");
        attach.Should().NotBeNull();
        attach!.Data.GetProperty("state").GetString().Should().Be("running");
        attach.Data.GetProperty("prompt").GetString().Should().Be("hello");
        // The text generated BEFORE this surface existed.
        attach.Data.GetProperty("text").GetString().Should().Be("watching ");

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AttachingWithNothingRunningSaysSo()
    {
        // A surface must be able to learn that nothing is happening, or it sits on a spinner because no
        // frame ever arrived.
        var factory = Factory(new ScriptedAssistant());
        var client = await AuthedAsync(factory, "quiet-user");

        await using var watching = await WatchAsync(client, "quiet-chat");

        var frame = await watching.NextOfAsync("turn.queue", "turn.attach");
        frame.Should().NotBeNull();
        frame!.Name.Should().Be("turn.queue");
        frame.Data.GetProperty("runningTurnId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnySurfaceCanStopTheTurn()
    {
        // Stop is a call, not a disconnect — a watcher holds no connection to abort.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var sender = await AuthedAsync(factory, "stop-user");
        var watcher = await AuthedAsync(factory, "stop-user");

        await using var watching = await WatchAsync(watcher, "stop-chat");
        await using var sending = await TurnAsync(sender, "stop-chat", "hello");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var attach = await watching.NextOfAsync("turn.attach");
        var turnId = attach!.Data.GetProperty("turnId").GetString();

        // The surface that did NOT start it ends it, and is not refused for that.
        var stopped = await watcher.DeleteAsync($"/turns/{turnId}");
        stopped.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Idempotent: pressing it twice is two people pressing one button, not an error.
        (await sender.DeleteAsync($"/turns/{turnId}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);

        // The sender's own stream ends with a terminal frame rather than hanging.
        var done = await sending.NextOfAsync("done");
        done.Should().NotBeNull();
    }

    [Fact]
    public async Task AnotherPersonsTurnIdIsNotAHandleOnIt()
    {
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var owner = await AuthedAsync(factory, "owner-user");
        var stranger = await AuthedAsync(factory, "stranger-user");

        await using var watching = await WatchAsync(owner, "private-chat");
        await using var sending = await TurnAsync(owner, "private-chat", "hello");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var attach = await watching.NextOfAsync("turn.attach");
        var turnId = attach!.Data.GetProperty("turnId").GetString();

        // Told there is no such turn, rather than that there is one they may not touch.
        (await stranger.DeleteAsync($"/turns/{turnId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task ASecondPromptQueuesRatherThanRacing()
    {
        // Two turns on one conversation would each read a history the other has not written yet.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "queue-user");

        await using var watching = await WatchAsync(client, "queue-chat");
        await using var first = await TurnAsync(client, "queue-chat", "first");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        await using var second = await TurnAsync(client, "queue-chat", "second");

        // The queue is visible to every attached surface, so nothing waits invisibly.
        var queue = await watching.QueueOfAsync(1);
        queue.Should().NotBeNull();
        queue!.Data.GetProperty("queued").EnumerateArray()
            .Select(q => q.GetProperty("prompt").GetString())
            .Should().ContainSingle().Which.Should().Be("second");

        // And only ONE turn has actually been offered to the model.
        script.Runs.Should().Be(1);

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AFourthPromptIsRefusedRatherThanQueuedForever()
    {
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "full-user");

        await using var running = await TurnAsync(client, "full-chat", "running");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        await using var q1 = await TurnAsync(client, "full-chat", "one");
        await using var q2 = await TurnAsync(client, "full-chat", "two");
        await using var q3 = await TurnAsync(client, "full-chat", "three");

        var refused = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(new { prompt = "four", conversationId = "full-chat" }),
        };
        refused.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await client.SendAsync(refused);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("queue_full");

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task StoppingTheRunningTurnLeavesTheQueueAlone()
    {
        // Interrupting a command does not discard what you typed ahead.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "keep-queue-user");

        await using var watching = await WatchAsync(client, "keep-chat");
        await using var first = await TurnAsync(client, "keep-chat", "first");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await using var second = await TurnAsync(client, "keep-chat", "second");

        var attach = await watching.NextOfAsync("turn.attach");
        var turnId = attach!.Data.GetProperty("turnId").GetString();

        (await client.DeleteAsync($"/turns/{turnId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The queued one runs: a second offer to the model is what proves it was not discarded.
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));
        script.Runs.Should().Be(2);

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AQueuedTurnCanBeCancelledOnItsOwn()
    {
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "cancel-queued-user");

        await using var watching = await WatchAsync(client, "cancel-chat");
        await using var first = await TurnAsync(client, "cancel-chat", "first");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await using var second = await TurnAsync(client, "cancel-chat", "second");

        var queue = await watching.QueueOfAsync(1);
        queue.Should().NotBeNull();
        var queuedId = queue!.Data.GetProperty("queued").EnumerateArray().Single()
            .GetProperty("turnId").GetString();

        (await client.DeleteAsync($"/turns/{queuedId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await watching.QueueOfAsync(0);
        after.Should().NotBeNull("cancelling a queued turn takes it off the queue for everyone");

        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AStyleTheCallerAsksForReachesTheTurn()
    {
        // The wire half of the spoken-reply shaping: kgsm-bot sends one JSON field on a voice-channel
        // turn, and this is the only thing standing between that field and the system prompt.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "voice-user");

        await using var sending = await Frames.OpenAsync(client,
            new HttpRequestMessage(HttpMethod.Post, "/turn")
            {
                Content = JsonContent.Create(new { prompt = "is it up", conversationId = "voice-chat", style = "voice" }),
            });
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        script.LastStyle.Should().Be(ReplyStyle.Voice);
        script.Release.TrySetResult();
    }

    [Fact]
    public async Task AStyleNobodyAskedForIsTheWrittenAnswer()
    {
        // Fails open on purpose: a surface that says nothing about how it renders gets the full answer,
        // which is readable everywhere. Silence must never be read as a request for a shorter one.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "plain-user");

        await using var sending = await TurnAsync(client, "plain-chat", "is it up");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        script.LastStyle.Should().Be(ReplyStyle.Default);
        script.Release.TrySetResult();
    }

    [Fact]
    public async Task TheEventStreamOnlyCarriesTurnFramesForTheConversationItIsAttachedTo()
    {
        // Turn frames arrive at token rate and mean nothing to a surface rendering something else.
        var script = new ScriptedAssistant();
        var factory = Factory(script);
        var client = await AuthedAsync(factory, "elsewhere-user");
        var sender = await AuthedAsync(factory, "elsewhere-user");

        await using var watching = await WatchAsync(client, "some-other-chat");
        await using var sending = await TurnAsync(sender, "the-busy-chat", "hello");
        await script.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // The state event for the OTHER conversation still arrives (that is about the chat list), but no
        // turn frame from a conversation this surface is not looking at — so a phone on a different chat
        // pays nothing for a reply it will never render.
        script.Release.TrySetResult();
        for (var i = 0; i < 8; i++)
        {
            var frame = await watching.NextAsync(TimeSpan.FromSeconds(2));
            if (frame is null) break;   // nothing more is coming, which is the point
            frame.Name.Should().NotBe("text.delta");
            frame.Name.Should().NotBe("turn.attach");
            frame.Name.Should().NotBe("done");
        }
    }
}
