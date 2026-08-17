using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Fetch;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The whole host's backups, in one call.
/// <para>
/// "Does anything need a backup?" spans every server, and answering it by calling the per-instance
/// read once each does not survive contact with the model: measured over three runs on eight servers
/// it read three or four and filled the rest in from nothing — ids and dates for servers that had been
/// backed up hours before. These hold the one-call answer that removes the incentive.
/// </para>
/// </summary>
public class FleetBackupsTests
{
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IConfirmationContext _confirmations = Substitute.For<IConfirmationContext>();
    private readonly ISearch _search = Substitute.For<ISearch>();
    private readonly IWebFetch _webFetch = Substitute.For<IWebFetch>();
    private readonly IServerMetrics _metrics = Substitute.For<IServerMetrics>();
    private readonly IEventHistory _events = Substitute.For<IEventHistory>();
    private readonly INetworkInfo _network = Substitute.For<INetworkInfo>();
    private readonly IUpnpInfo _upnp = Substitute.For<IUpnpInfo>();
    private readonly IHostFacts _hostFacts = Substitute.For<IHostFacts>();
    private readonly IBlueprintAuthoring _blueprintAuthoring = Substitute.For<IBlueprintAuthoring>();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public FleetBackupsTests()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["Ketchup"] = "palworld",
                ["necesse"] = "necesse",
                ["starbound"] = "starbound",
                ["fresh"] = "factorio",
                ["broken"] = "terraria",
            });
    }

    private ToolDispatcher Create(IServerFacts facts) =>
        new(_operations, _inventory, _confirmations, _search, _webFetch, _metrics, _events,
            _network, _upnp, facts, _hostFacts, _blueprintAuthoring, ShippedText.Catalog,
            new SettlementTiming(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10)),
            new InMemoryMemoryStore(), Options.Create(new MemoryOptions()),
            NullLogger<ToolDispatcher>.Instance);

    /// <summary>The fleet read is the call with no instance_name.</summary>
    private async Task<string> RunFleet(IServerFacts facts) =>
        (await Create(facts).ExecuteAsync(new LlmToolCall(
            ShippedText.Name(LlmTools.ListInstanceBackups),
            new Dictionary<string, string?>()))).Summary;

    private static BackupEntry Aged(string instance, TimeSpan ago, long size = 1_000_000_000) =>
        new($"{instance}-{Now.Add(-ago):yyyyMMdd'T'HHmmss'Z'}-abc123",
            Version: "1",
            CreatedAt: Now.Add(-ago),
            SizeBytes: size,
            Consistency: "flushed",
            Contents: new[] { "install", "saves" },
            FileCount: 10);

    private static PerInstanceFacts Fleet() => new(new Dictionary<string, BackupListing>
    {
        ["Ketchup"] = new(FactsState.Available, new[] { Aged("Ketchup", TimeSpan.FromHours(10)) }),
        ["necesse"] = new(FactsState.Available, new[]
        {
            Aged("necesse", TimeSpan.FromDays(9)),
            Aged("necesse", TimeSpan.FromDays(20)),
        }),
        ["starbound"] = new(FactsState.Available, new[] { Aged("starbound", TimeSpan.FromDays(2)) }),
        ["fresh"] = new(FactsState.Available, []),
        ["broken"] = new(FactsState.Unavailable, []),
    });

    [Fact]
    public async Task OneCall_AnswersForEveryInstalledServer()
    {
        var summary = await RunFleet(Fleet());

        foreach (var name in new[] { "Ketchup", "necesse", "starbound", "fresh", "broken" })
            summary.Should().Contain(name);
    }

    [Fact]
    public async Task TheServerLongestSinceItsLastBackup_ComesFirst()
    {
        var summary = await RunFleet(Fleet());

        // necesse (9 days) before starbound (2 days) before Ketchup (10 hours). The question is asked
        // in this order, so the answer is given in it.
        summary.IndexOf("necesse", StringComparison.Ordinal).Should()
            .BeLessThan(summary.IndexOf("starbound", StringComparison.Ordinal));
        summary.IndexOf("starbound", StringComparison.Ordinal).Should()
            .BeLessThan(summary.IndexOf("Ketchup", StringComparison.Ordinal));
    }

    /// <summary>
    /// A server with nothing to restore is the strongest answer to the question, so it leads — and it
    /// is worded as having none, not as an age of zero.
    /// </summary>
    [Fact]
    public async Task AServerWithNoBackups_IsNamedAsHavingNone_AndComesBeforeTheRest()
    {
        var summary = await RunFleet(Fleet());

        summary.Should().Contain("fresh — no backups at all");
        summary.IndexOf("fresh", StringComparison.Ordinal).Should()
            .BeLessThan(summary.IndexOf("necesse", StringComparison.Ordinal));
    }

    /// <summary>
    /// The never-fabricate rule, in the shape this tool can break it: an unreadable listing is not an
    /// empty one, and a server reported as having no backups when nobody could look is exactly the
    /// answer that gets a world deleted.
    /// </summary>
    [Fact]
    public async Task AnUnreadableServer_IsUnknown_NotEmpty()
    {
        var summary = await RunFleet(Fleet());

        summary.Should().Contain("broken — could not be read");
        summary.Should().NotContain("broken — no backups");
    }

    [Fact]
    public async Task EachServerIsReadExactlyOnce()
    {
        var facts = Fleet();

        await RunFleet(facts);

        facts.Calls.Should().BeEquivalentTo(
            new[] { "Ketchup", "necesse", "starbound", "fresh", "broken" });
    }

    [Fact]
    public async Task ItPointsAtThePerInstanceReadForTheIds()
    {
        var summary = await RunFleet(Fleet());

        summary.Should().Contain(ShippedText.Name(LlmTools.ListInstanceBackups).ToString());
    }

    [Fact]
    public async Task WithNothingInstalled_ItSaysSo_RatherThanListingNothing()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var summary = await RunFleet(Fleet());

        summary.Should().Contain("No servers are installed");
    }

    /// <summary>
    /// A backup that holds no saves directory restores an install and not a world. The per-instance
    /// read carries that warning; the fleet read gathers it rather than repeating it on every line.
    /// </summary>
    [Fact]
    public async Task TheSavesWarning_NamesOnlyTheServersItAppliesTo()
    {
        var installOnly = new BackupEntry(
            "starbound-x", "1", Now.AddDays(-2), 1000, "flushed", new[] { "install" }, 5);
        var facts = new PerInstanceFacts(new Dictionary<string, BackupListing>
        {
            ["Ketchup"] = new(FactsState.Available, new[] { Aged("Ketchup", TimeSpan.FromHours(10)) }),
            ["starbound"] = new(FactsState.Available, new[] { installOnly }),
        });
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["Ketchup"] = "palworld",
                ["starbound"] = "starbound",
            });

        var summary = await RunFleet(facts);

        summary.Should().Contain("saves directory");
        summary.Should().Contain("starbound");
        // Ketchup's backups DO hold saves, so it must not be swept into the warning.
        var warning = summary[summary.IndexOf("None of the backups", StringComparison.Ordinal)..];
        warning.Should().NotContain("Ketchup");
    }

    /// <summary>
    /// Per-instance facts, so the fan-out can be observed and each server can differ. The other legs
    /// stay unavailable — a leg this test did not arrange must read as unknown, never as an empty
    /// world something might assert on.
    /// </summary>
    private sealed class PerInstanceFacts : IServerFacts
    {
        private readonly IReadOnlyDictionary<string, BackupListing> _byInstance;
        private readonly List<string> _calls = [];

        public PerInstanceFacts(IReadOnlyDictionary<string, BackupListing> byInstance) =>
            _byInstance = byInstance;

        public IReadOnlyList<string> Calls => _calls;

        public Task<BackupListing> GetBackupsAsync(string i, CancellationToken ct = default)
        {
            lock (_calls) _calls.Add(i);
            return Task.FromResult(_byInstance.TryGetValue(i, out var listing)
                ? listing
                : new BackupListing(FactsState.Unavailable, []));
        }

        public Task<InstanceConfigFacts> GetConfigAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new InstanceConfigFacts(FactsState.Unavailable, []));
        public Task<NoteFacts> GetNoteAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new NoteFacts(FactsState.Unavailable, null, null, null));
        public Task<InstanceStatusFacts> GetStatusAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new InstanceStatusFacts(
                FactsState.Unavailable, false, null, null, null, null, null, null, [], null, null, null, 0));
        public Task<VersionFacts> GetVersionAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new VersionFacts(FactsState.Unavailable, null, null, null));
        public Task<PresenceReading> GetPresenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new PresenceReading(FactsState.Unavailable, []));
        public Task<AutostartReading> GetAutostartAsync(CancellationToken ct = default) =>
            Task.FromResult(new AutostartReading(FactsState.Unavailable, []));
        public Task<ConsoleTail> GetConsoleTailAsync(string i, int lines, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleTail(FactsState.Unavailable, []));
        public Task<ConsoleRuns> GetConsoleRunsAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleRuns(FactsState.Unavailable, []));
        public Task<ConsoleTail> GetConsoleRunTailAsync(
            string i, int lines, int run, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleTail(FactsState.Unavailable, []));
    }
}
