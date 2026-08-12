using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// <see cref="SqlitePendingConfirmationStore"/> over a throwaway temp DB (the same file
/// <see cref="SqliteConversationStore"/> would use — this store just adds its own table): what a handle
/// redeems, who may redeem it, and when it stops being redeemable.
/// </summary>
/// <remarks>
/// This store holds every action the assistant proposes before a human approves it, so these tests are
/// as much about what it <em>refuses</em> as what it returns.
/// </remarks>
public sealed class SqlitePendingConfirmationStoreTests : IDisposable
{
    private const string Owner = "245717107596197888";
    private const string Someone = "385730677141929985";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-pending-confirmations-{Guid.NewGuid():N}.db");

    private SqlitePendingConfirmationStore Create() =>
        new(Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

    private static DateTimeOffset InFiveMinutes => DateTimeOffset.UtcNow.AddMinutes(5);

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void AHandleRedeemsTheOperationItWasStagedFor()
    {
        var store = Create();
        var staged = new PendingConfirmation(
            ConfirmationKind.SetConfig, "factorio", InstanceName: null,
            ConfigKey: "executable_arguments", ConfigValue: "--start-server-load-latest");

        var handle = store.Put(staged, Owner, InFiveMinutes);

        store.TryTake(handle, Owner, out var taken).Should().BeTrue();
        taken.Should().Be(staged);
    }

    /// <summary>
    /// The whole point of holding the operation here: a file body has no size to fit into and no
    /// encoding to survive, because it never leaves the host.
    /// </summary>
    [Fact]
    public void AFileBodyRoundTripsWhole()
    {
        var store = Create();
        var body = string.Join('\n', Enumerable.Range(0, 50_000).Select(i => $"line {i} — ünicode ✓"));
        var handle = store.Put(
            new PendingConfirmation(ConfirmationKind.WriteFile, "minecraft",
                ConfigKey: "server.properties", ConfigValue: body),
            Owner, InFiveMinutes);

        store.TryTake(handle, Owner, out var taken).Should().BeTrue();
        taken.ConfigValue.Should().Be(body);
    }

    /// <summary>
    /// Approving something twice — a double-clicked button, a retried request — is running once what
    /// the user asked for once.
    /// </summary>
    [Fact]
    public void RedemptionIsSingleUse()
    {
        var store = Create();
        var handle = store.Put(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), Owner, InFiveMinutes);

        store.TryTake(handle, Owner, out _).Should().BeTrue();
        store.TryTake(handle, Owner, out _).Should().BeFalse();
    }

    /// <summary>
    /// Somebody else's handle is refused — and, just as importantly, left standing. Consuming it would
    /// let anyone who guessed at a handle cancel an action its owner is looking at.
    /// </summary>
    [Fact]
    public void AnotherUserCannotRedeemIt_AndDoesNotDestroyIt()
    {
        var store = Create();
        var handle = store.Put(new PendingConfirmation(ConfirmationKind.Stop, "factorio"), Owner, InFiveMinutes);

        store.TryTake(handle, Someone, out _).Should().BeFalse();
        store.TryTake(handle, Owner, out var taken).Should().BeTrue();
        taken.Target.Should().Be("factorio");
    }

    [Fact]
    public void APastItsLifetimeHandleIsRefused()
    {
        var store = Create();
        var handle = store.Put(
            new PendingConfirmation(ConfirmationKind.Start, "factorio"),
            Owner, DateTimeOffset.UtcNow.AddSeconds(-1));

        store.TryTake(handle, Owner, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000000000000000000000000000")]
    public void AHandleThisStoreNeverIssuedIsRefused(string? handle)
    {
        Create().TryTake(handle, Owner, out _).Should().BeFalse();
    }

    /// <summary>
    /// A caller with no identity redeems nothing. Every handle is staged for someone, so an empty user
    /// can only ever be a caller the endpoint failed to identify.
    /// </summary>
    [Fact]
    public void AnUnidentifiedCallerRedeemsNothing()
    {
        var store = Create();
        var handle = store.Put(new PendingConfirmation(ConfirmationKind.Start, "factorio"), Owner, InFiveMinutes);

        store.TryTake(handle, "", out _).Should().BeFalse();
    }

    /// <summary>
    /// The handle is the capability, so it carries nothing about what it redeems and is not something
    /// a caller can construct: 32 hex characters from the cryptographic RNG, which also leaves it
    /// inside every surface's identifier limits.
    /// </summary>
    [Fact]
    public void HandlesAreOpaque_Unique_AndShortEnoughForAnySurface()
    {
        var store = Create();
        var handles = Enumerable.Range(0, 200)
            .Select(_ => store.Put(
                new PendingConfirmation(ConfirmationKind.Uninstall, "terraria"), Owner, InFiveMinutes))
            .ToArray();

        handles.Should().OnlyHaveUniqueItems();
        handles.Should().OnlyContain(h => Regex.IsMatch(h, "^[0-9a-f]{32}$"));
        handles.Should().OnlyContain(h => h.Length <= 100);
        handles.Should().NotContain(h => h.Contains("terraria", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A staged action outlives a restart of the service holding it — an operator restarting the
    /// assistant does not silently void a confirmation somebody is looking at.
    /// </summary>
    [Fact]
    public void AStagedActionSurvivesARestart()
    {
        var handle = Create().Put(
            new PendingConfirmation(ConfirmationKind.Install, "valheim", InstanceName: "valheim-2"),
            Owner, InFiveMinutes);

        Create().TryTake(handle, Owner, out var taken).Should().BeTrue();
        taken.Kind.Should().Be(ConfirmationKind.Install);
        taken.InstanceName.Should().Be("valheim-2");
    }

    [Fact]
    public void StagingSweepsWhatHasAlreadyExpired()
    {
        var store = Create();
        var stale = store.Put(
            new PendingConfirmation(ConfirmationKind.Stop, "factorio"), Owner, DateTimeOffset.UtcNow.AddSeconds(-1));

        // Any later staging sweeps it, so an abandoned proposal is not kept indefinitely.
        store.Put(new PendingConfirmation(ConfirmationKind.Start, "terraria"), Owner, InFiveMinutes);

        store.TryTake(stale, Owner, out _).Should().BeFalse();
    }

    // --- Restating what is still waiting -------------------------------------------------------
    //
    // A proposal reaches a client as a live `command.proposed` frame, which only the surfaces attached
    // at the time ever see. Everything below is about the surface that arrives AFTERWARDS — a reload, a
    // second device, or the one following the push notification that announced it, which is by
    // definition the surface that was not there.

    private const string Chat = "web:245717107596197888:abc123";

    [Fact]
    public void AWaitingProposalIsRestatedToItsOwnConversation()
    {
        var store = Create();
        var handle = store.Put(
            new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"),
            Owner, InFiveMinutes, conversationId: Chat);

        var pending = store.PendingFor(Owner, Chat);

        pending.Should().ContainSingle();
        pending[0].Handle.Should().Be(handle, "the restated card must carry the handle that redeems it");
        pending[0].Confirmation.Kind.Should().Be(ConfirmationKind.Backup);
        pending[0].Confirmation.Target.Should().Be("projectzomboid");
    }

    [Fact]
    public void AProposalStagedWithNoConversationIsRestatedNowhere()
    {
        // The buffered /turn is kgsm-bot's. Its confirmations belong to a Discord message, not to a
        // conversation any browser can open.
        var store = Create();
        store.Put(new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"), Owner, InFiveMinutes);

        store.PendingFor(Owner, Chat).Should().BeEmpty();
    }

    [Fact]
    public void AnExpiredProposalIsNotRestated()
    {
        // Drawing it would offer a button that cannot work — worse than not drawing it, because the
        // person would spend their remaining seconds pressing it.
        var store = Create();
        store.Put(
            new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"),
            Owner, DateTimeOffset.UtcNow.AddSeconds(-1), conversationId: Chat);

        store.PendingFor(Owner, Chat).Should().BeEmpty();
    }

    [Fact]
    public void AProposalAlreadyApprovedIsNotRestated()
    {
        var store = Create();
        var handle = store.Put(
            new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"),
            Owner, InFiveMinutes, conversationId: Chat);

        store.TryTake(handle, Owner, out _).Should().BeTrue();

        store.PendingFor(Owner, Chat).Should().BeEmpty();
    }

    [Fact]
    public void AProposalIsNeverRestatedIntoAnotherConversation()
    {
        var store = Create();
        store.Put(
            new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"),
            Owner, InFiveMinutes, conversationId: Chat);

        store.PendingFor(Owner, "web:245717107596197888:somewhere-else").Should().BeEmpty();
    }

    [Fact]
    public void AProposalIsNeverRestatedToSOMEBODYELSE()
    {
        // The conversation id is derived from the caller's own principal upstream, so this is a second
        // lock on the same door: a scoping mistake there still cannot hand over somebody else's action.
        var store = Create();
        store.Put(
            new PendingConfirmation(ConfirmationKind.Backup, "projectzomboid"),
            Owner, InFiveMinutes, conversationId: Chat);

        store.PendingFor(Someone, Chat).Should().BeEmpty();
    }
}
