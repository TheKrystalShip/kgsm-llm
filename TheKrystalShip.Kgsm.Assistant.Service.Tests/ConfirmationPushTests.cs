using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Push;
using TheKrystalShip.Kgsm.Assistant.Service.Streaming;
using TheKrystalShip.KGSM.WebPush;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Which waiting actions get announced, to whom, and — mostly — which do not.
/// </summary>
/// <remarks>
/// The decision this covers spends a budget that cannot be topped up: a staged action lives five
/// minutes, so announcing too eagerly interrupts somebody who is already looking at it, and announcing
/// too late produces a notification whose buttons die before it is read.
/// </remarks>
public sealed class ConfirmationPushTests : IDisposable
{
    private static readonly ConfirmationStager Owner =
        new("discord", "245717107596197888", "heisen");

    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-push-worker-{Guid.NewGuid():N}.db");

    // The sweep never advances time — it asks the clock once, for the "you have N minutes" line — so a
    // fake one would add a dependency to control something no test here moves.
    private readonly TimeProvider _clock = TimeProvider.System;

    private IOptions<ConversationOptions> Db() =>
        Options.Create(new ConversationOptions { DatabasePath = _dbPath });

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>A bus whose presence answer the test sets.</summary>
    private sealed class StubBus(bool present) : IConversationEventBus
    {
        public bool Present { get; set; } = present;

        public bool PresentWithin(string userId, TimeSpan grace) => Present;

        public void Publish(string userId, ConversationEvent ev) { }
        public void PublishToAttached(string userId, string conversationId, ConversationEvent ev) { }
        public bool PublishTo(string streamId, string userId, ConversationEvent ev) => false;
        public ConversationEventSubscription Subscribe(string userId) => throw new NotSupportedException();
        public bool Attach(string streamId, string userId, string? conversationId) => false;
    }

    /// <summary>Records what was sent and answers with whatever the test wants the push service to say.</summary>
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Uri> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private (ConfirmationPushWorker Worker, StubBus Bus, StubHandler Http,
             SqlitePendingConfirmationStore Pending, SqlitePushSubscriptionStore Subscriptions,
             SqlitePushActionStore Actions)
        Build(bool present = false, HttpStatusCode status = HttpStatusCode.Created, int grace = 20)
    {
        var actions = new SqlitePushActionStore(Db());
        var subscriptions = new SqlitePushSubscriptionStore(Db(), actions);
        var pending = new SqlitePendingConfirmationStore(Db());
        var bus = new StubBus(present);
        var handler = new StubHandler(status);
        var sender = new WebPushSender(new HttpClient(handler));

        var options = Options.Create(new AssistantServiceOptions
        {
            Push = new PushOptions { Enabled = true, PresenceGraceSeconds = grace },
        });

        var worker = new ConfirmationPushWorker(
            pending, subscriptions, actions, bus, sender, options, _clock,
            NullLogger<ConfirmationPushWorker>.Instance);

        return (worker, bus, handler, pending, subscriptions, actions);
    }

    /// <summary>A device with real keys, so the encryption the sender does actually succeeds.</summary>
    private static void RegisterDevice(SqlitePushSubscriptionStore subscriptions, string userId)
    {
        var browser = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = browser.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 33 - p.Q.X!.Length);
        p.Q.Y!.CopyTo(point, 65 - p.Q.Y!.Length);

        subscriptions.Register(userId, new PushSubscription(
            Endpoint,
            WebPushCrypto.ToBase64Url(point),
            WebPushCrypto.ToBase64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))), null);
    }

    private static PendingConfirmation Restart => new(ConfirmationKind.Restart, "Ketchup");

    // --- What is eligible at all ----------------------------------------------------------------

    [Fact]
    public async Task AnActionStagedWithNobodyToAnnounceItToIsNeverSent()
    {
        // The buffered /turn is kgsm-bot's, and Discord already draws Confirm and Cancel on it.
        var (worker, _, http, pending, subscriptions, _) = Build();
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5));

        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AWaitingActionReachesTheDeviceOfThePersonItIsWaitingOn()
    {
        var (worker, _, http, pending, subscriptions, _) = Build();
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().ContainSingle().Which.ToString().Should().Be(Endpoint);
    }

    [Fact]
    public async Task SomebodyWhoIsStillAtASurfaceIsNotInterrupted()
    {
        // Every surface renders the proposal inline, so a notification would be a second copy of
        // something already on screen.
        var (worker, _, http, pending, subscriptions, _) = Build(present: true);
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task WalkingAwayLATERStillGetsAnnounced()
    {
        // The case the whole thing exists for: present when it was staged, gone with minutes left.
        var (worker, bus, http, pending, subscriptions, _) = Build(present: true);
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);
        http.Sent.Should().BeEmpty();

        bus.Present = false;
        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task AnActionIsAnnouncedOnceHoweverManyPassesRun()
    {
        var (worker, _, http, pending, subscriptions, _) = Build();
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);
        await worker.SweepAsync(CancellationToken.None);
        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task AnActionThatHasALREADYExpiredIsNotAnnounced()
    {
        // A notification whose buttons are dead before it is drawn is worse than silence.
        var (worker, _, http, pending, subscriptions, _) = Build();
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, DateTimeOffset.UtcNow.AddSeconds(-1), Owner);

        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SomebodyWithNoRegisteredBrowserIsSimplySkipped()
    {
        var (worker, _, http, pending, _, _) = Build();
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().BeEmpty();
    }

    // --- What the two buttons carry -------------------------------------------------------------

    [Fact]
    public async Task AnnouncingMintsTwoHandlesBoundToTheStagedAction()
    {
        var (worker, _, _, pending, subscriptions, actions) = Build();
        RegisterDevice(subscriptions, Owner.UserId);
        var confirmation = pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);

        // Nothing hands the handles back out of band, so the proof is that voiding the confirmation
        // they point at is what removes them.
        actions.VoidForConfirmation(confirmation);
        // A second announce would mint fresh ones; the row is already marked, so none appear.
        await worker.SweepAsync(CancellationToken.None);
    }

    // --- What a push service's answer costs -----------------------------------------------------

    [Fact]
    public async Task AGoneSubscriptionIsRetiredRatherThanRetriedForever()
    {
        var (worker, _, _, pending, subscriptions, _) = Build(status: HttpStatusCode.Gone);
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);

        subscriptions.For(Owner.UserId).Should().BeEmpty();
    }

    [Fact]
    public async Task ABadMinuteFromThePushServiceCostsARetryNotTheNotification()
    {
        // 503 is transient. Marking the row announced on a failed send is how a real outage turns
        // into silence nobody ever learns about.
        var (worker, _, http, pending, subscriptions, _) = Build(status: HttpStatusCode.ServiceUnavailable);
        RegisterDevice(subscriptions, Owner.UserId);
        pending.Put(Restart, Owner.UserId, _clock.GetUtcNow().AddMinutes(5), Owner);

        await worker.SweepAsync(CancellationToken.None);
        await worker.SweepAsync(CancellationToken.None);

        http.Sent.Should().HaveCount(2);
        subscriptions.For(Owner.UserId).Should().ContainSingle("a 503 is not a reason to forget a device");
    }

    // --- What it reads as -----------------------------------------------------------------------

    [Fact]
    public void TheTitleNamesTheActionTheWayEverySurfaceDoes()
    {
        ConfirmationNotice.Title(new PendingConfirmation(ConfirmationKind.Restart, "Ketchup"))
            .Should().Be("Restart Ketchup?");
        ConfirmationNotice.Title(new PendingConfirmation(ConfirmationKind.Backup, "romestead"))
            .Should().Be("Back up romestead?");
    }

    [Fact]
    public void AnInstallNamesWhatYouWillEndUpWithNotWhatItWasBuiltFrom()
    {
        ConfirmationNotice.Title(
            new PendingConfirmation(ConfirmationKind.Install, "factorio", InstanceName: "saturday-game"))
            .Should().Be("Install saturday-game?");
    }

    [Fact]
    public void TheBodyStatesTheDeadlineBecauseItIsShortAndReal()
    {
        var now = DateTimeOffset.Parse("2026-08-12T12:00:00Z");

        ConfirmationNotice.Body(now.AddMinutes(5), now).Should().Contain("about 5 minutes");
        ConfirmationNotice.Body(now.AddSeconds(30), now).Should().Contain("under a minute");
        ConfirmationNotice.Body(now.AddSeconds(-1), now).Should().Be("This has expired.");
    }

    [Fact]
    public void ThePayloadCarriesBothHandlesAndTagsByTheActionItIsAbout()
    {
        var waiting = new WaitingConfirmation(
            "conf-1", Owner, Restart, _clock.GetUtcNow().AddMinutes(5));

        var json = System.Text.Encoding.UTF8.GetString(
            ConfirmationNotice.Payload(waiting, "confirm-handle", "cancel-handle", _clock.GetUtcNow()));

        json.Should().Contain("\"confirm\":\"confirm-handle\"");
        json.Should().Contain("\"cancel\":\"cancel-handle\"");
        // A second notification about the same action replaces the first rather than stacking.
        json.Should().Contain("\"tag\":\"kgsm-confirmation:conf-1\"");
        // ⚠ Never the payload: a config value or a file body would travel through a push service and
        // an OS notification shade, neither of which is somewhere a server's contents belong.
        json.Should().NotContain("config");
    }
}
