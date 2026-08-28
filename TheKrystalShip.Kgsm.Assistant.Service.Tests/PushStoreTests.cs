using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Push;
using TheKrystalShip.KGSM.WebPush;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The two stores behind a notification's buttons, over a throwaway temp DB.
/// </summary>
/// <remarks>
/// A push handle is a capability that acts without a session, so what these tests pin is mostly what
/// the stores <em>refuse</em>: a second use, a lapsed one, and one belonging to a device that has gone.
/// </remarks>
public sealed class PushStoreTests : IDisposable
{
    private static readonly ConfirmationStager Owner =
        new("discord", "245717107596197888", "heisen");

    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";
    private const string OtherEndpoint = "https://fcm.googleapis.com/fcm/send/zzz999";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-push-{Guid.NewGuid():N}.db");

    private IOptions<ConversationOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ConversationOptions { DatabasePath = _dbPath });

    private SqlitePushActionStore Actions() => new(Options());

    private SqlitePushSubscriptionStore Subscriptions(IPushActionStore actions) =>
        new(Options(), actions);

    private static DateTimeOffset InFiveMinutes => DateTimeOffset.UtcNow.AddMinutes(5);

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    // --- The handle a button carries ------------------------------------------------------------

    [Fact]
    public void AHandleRedeemsTheConfirmationAndAccountItWasMintedFor()
    {
        var actions = Actions();
        var handle = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);

        actions.TryTake(handle, out var action).Should().BeTrue();
        action.Verb.Should().Be(PushActionVerb.Confirm);
        action.ConfirmationHandle.Should().Be("conf-1");
        action.Stager.Should().Be(Owner);
        action.Endpoint.Should().Be(Endpoint);
    }

    [Fact]
    public void AHandleIsSingleUse()
    {
        // A notification button is exactly the thing somebody double-taps.
        var actions = Actions();
        var handle = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);

        actions.TryTake(handle, out _).Should().BeTrue();
        actions.TryTake(handle, out _).Should().BeFalse();
    }

    [Fact]
    public void TakingOneButtonVoidsTheOther()
    {
        // Confirm settles the question Cancel was asking. Leaving Cancel's handle standing would be a
        // live capability over an action that has already been decided.
        var actions = Actions();
        var confirm = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);
        var cancel = actions.Mint(PushActionVerb.Cancel, "conf-1", Owner, Endpoint, InFiveMinutes);

        actions.TryTake(confirm, out _).Should().BeTrue();
        actions.TryTake(cancel, out _).Should().BeFalse();
    }

    [Fact]
    public void ButtonsForADIFFERENTConfirmationSurviveIntact()
    {
        var actions = Actions();
        var mine = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);
        var theirs = actions.Mint(PushActionVerb.Confirm, "conf-2", Owner, Endpoint, InFiveMinutes);

        actions.TryTake(mine, out _).Should().BeTrue();
        actions.TryTake(theirs, out _).Should().BeTrue();
    }

    [Fact]
    public void AnExpiredHandleIsRefusedAndConsumed()
    {
        // Consumed even though it was refused: a row that could be read a second time is a retry of
        // something the first read already decided.
        var actions = Actions();
        var handle = actions.Mint(
            PushActionVerb.Confirm, "conf-1", Owner, Endpoint, DateTimeOffset.UtcNow.AddSeconds(-1));

        actions.TryTake(handle, out _).Should().BeFalse();
        actions.TryTake(handle, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-handle-anyone-minted")]
    public void AnUnusableHandleIsSimplyRefused(string? handle)
    {
        Actions().TryTake(handle, out _).Should().BeFalse();
    }

    [Fact]
    public void ForgettingADeviceVoidsWhatWasStagedForIt()
    {
        var actions = Actions();
        var mine = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);
        var elsewhere = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, OtherEndpoint, InFiveMinutes);

        actions.VoidFor(Endpoint);

        actions.TryTake(mine, out _).Should().BeFalse();
        // The same action on a device that is still registered is untouched — retiring one browser is
        // not a statement about another.
        actions.TryTake(elsewhere, out _).Should().BeTrue();
    }

    [Fact]
    public void SettlingAConfirmationElsewhereVoidsItsButtonsEverywhere()
    {
        var actions = Actions();
        var phone = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);
        var tablet = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, OtherEndpoint, InFiveMinutes);

        actions.VoidForConfirmation("conf-1");

        actions.TryTake(phone, out _).Should().BeFalse();
        actions.TryTake(tablet, out _).Should().BeFalse();
    }

    // --- The devices, and the identity they were registered against -----------------------------

    [Fact]
    public void TheVapidPairIsGeneratedOnceAndNeverChanges()
    {
        // The whole feature rests on this. A subscription is bound at creation to the key it was
        // handed, so a second pair does not rotate anything — it orphans every device already
        // registered, silently and with no error at either end.
        var first = Subscriptions(Actions()).Keys();
        var second = Subscriptions(Actions()).Keys();

        second.PublicKey.Should().Be(first.PublicKey);
        second.PrivateKey.Should().Be(first.PrivateKey);
    }

    [Fact]
    public void TheGeneratedKeyIsTheShapeABrowserAccepts()
    {
        var keys = Subscriptions(Actions()).Keys();
        var raw = WebPushCrypto.FromBase64Url(keys.PublicKey);

        raw.Should().HaveCount(65);
        raw[0].Should().Be(0x04);
    }

    [Fact]
    public void RegisteringTheSameBrowserTwiceLeavesOneDevice()
    {
        // Re-posting is the ordinary case: the client re-registers whenever it finds a live
        // subscription the host may not know about, which is what heals a row lost to a restore.
        var store = Subscriptions(Actions());
        store.Register(Owner.UserId, new PushSubscription(Endpoint, "key-1", "auth-1"), null);
        store.Register(Owner.UserId, new PushSubscription(Endpoint, "key-2", "auth-2"), null);

        var devices = store.For(Owner.UserId);
        devices.Should().ContainSingle();
        devices[0].Subscription.P256dh.Should().Be("key-2");
    }

    [Fact]
    public void ABrowserSignedIntoASecondAccountBelongsToWhoeverRegisteredItLast()
    {
        // Keyed by endpoint rather than by user: one browser is one device, and it must not end up
        // notified about two people's actions.
        var store = Subscriptions(Actions());
        store.Register(Owner.UserId, new PushSubscription(Endpoint, "k", "a"), null);
        store.Register("someone-else", new PushSubscription(Endpoint, "k", "a"), null);

        store.For(Owner.UserId).Should().BeEmpty();
        store.For("someone-else").Should().ContainSingle();
    }

    [Fact]
    public void ForgettingSomebodyElsesDeviceDoesNothing()
    {
        var store = Subscriptions(Actions());
        store.Register(Owner.UserId, new PushSubscription(Endpoint, "k", "a"), null);

        store.Unregister("someone-else", Endpoint).Should().BeFalse();
        store.For(Owner.UserId).Should().ContainSingle();
    }

    [Fact]
    public void RetiringAGoneDeviceTakesItsStagedButtonsWithIt()
    {
        var actions = Actions();
        var store = Subscriptions(actions);
        store.Register(Owner.UserId, new PushSubscription(Endpoint, "k", "a"), null);
        var handle = actions.Mint(PushActionVerb.Confirm, "conf-1", Owner, Endpoint, InFiveMinutes);

        store.Retire(Endpoint);

        store.For(Owner.UserId).Should().BeEmpty();
        actions.TryTake(handle, out _).Should().BeFalse();
    }
}
