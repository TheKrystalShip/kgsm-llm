using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Fetch;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// What the per-instance reads actually SAY.
/// <para>
/// The model reads one thing: the summary text. Every fact a tool holds and does not write into that
/// string is a fact the model does not have, however faithfully the port carried it — which is what
/// these assert. They are deliberately about wording, because for a tool result the wording IS the
/// contract.
/// </para>
/// </summary>
public class InstanceReadOutputTests
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

    public InstanceReadOutputTests()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Ketchup"] = "palworld" });
        _inventory.GetBlueprintCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BlueprintSummary>());
    }

    private ToolDispatcher Create(IServerFacts facts) =>
        new(_operations, _inventory, _confirmations, _search, _webFetch, _metrics, _events,
            _network, _upnp, facts, _hostFacts, _blueprintAuthoring, ShippedText.Catalog,
            new SettlementTiming(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10)),
            NullLogger<ToolDispatcher>.Instance);

    private async Task<string> Run(Capability capability, IServerFacts facts) =>
        (await Create(facts).ExecuteAsync(new LlmToolCall(
            ShippedText.Name(capability),
            new Dictionary<string, string?> { ["instance_name"] = "Ketchup" }))).Summary;

    // --- backups ---

    [Fact]
    public async Task Backups_SayWhatEachOneHolds_NotJustItsName()
    {
        var facts = new StubFacts
        {
            Backups = new BackupListing(FactsState.Available, new[]
            {
                new BackupEntry(
                    "Ketchup-20260817T023033Z-c6d089",
                    Version: "24575149",
                    CreatedAt: DateTimeOffset.UtcNow.AddHours(-9),
                    SizeBytes: 4_890_465_301,
                    Consistency: "flushed",
                    Contents: new[] { "install", "saves" },
                    FileCount: 343),
            }),
        };

        var output = await Run(LlmTools.ListInstanceBackups, facts);

        // The id stays, because a restore names it. Everything a person actually asks about a
        // backup is now beside it.
        output.Should().Contain("Ketchup-20260817T023033Z-c6d089")
            .And.Contain("9 hours ago")          // relative, because the model holds no clock
            .And.Contain("game version 24575149")
            .And.Contain("4.6 GB")               // not "4663.9 MB"
            .And.Contain("343 files")
            .And.Contain("holds: install, saves")
            .And.Contain("capture: flushed");
    }

    [Fact]
    public async Task Backups_HoldingNoSaves_SayThatRestoringOneRestoresNoWorld()
    {
        var facts = new StubFacts
        {
            Backups = new BackupListing(FactsState.Available, new[]
            {
                new BackupEntry("b1", null, DateTimeOffset.UtcNow.AddDays(-1), 1024, null,
                    Contents: new[] { "install" }, FileCount: 3),
            }),
        };

        var output = await Run(LlmTools.ListInstanceBackups, facts);

        output.Should().Contain("holds: install")
            .And.Contain("restoring one restores the installation and not the world");
    }

    [Fact]
    public async Task Backups_UnreadableIsNotReportedAsNone()
    {
        var facts = new StubFacts { Backups = new BackupListing(FactsState.Unavailable, []) };

        var output = await Run(LlmTools.ListInstanceBackups, facts);

        output.Should().Contain("isn't the same as it having none");
    }

    // --- acting on a backup by id ---

    private async Task<string> BackupCommand(string verb, string id, IServerFacts facts) =>
        (await Create(facts).ExecuteAsync(new LlmToolCall(
            ShippedText.Name(LlmTools.BackupCommand),
            new Dictionary<string, string?>
            {
                ["instance_name"] = "Ketchup",
                ["verb"] = verb,
                ["backup_name"] = id,
            }))).Summary;

    private static StubFacts TwoBackups() => new()
    {
        Backups = new BackupListing(FactsState.Available, new[]
        {
            new BackupEntry("Ketchup-20260817T023033Z-c6d089", null,
                DateTimeOffset.UtcNow.AddHours(-9), 1024, null, new[] { "install" }, 3),
            new BackupEntry("Ketchup-20260816T023018Z-25d926", null,
                DateTimeOffset.UtcNow.AddDays(-1), 1024, null, new[] { "install" }, 3),
        }),
    };

    [Fact]
    public async Task ATruncatedBackupId_ResolvesToTheOneItCanOnlyMean()
    {
        // The measured failure: the model quotes an id back without its hash suffix. Exactly one
        // backup starts with what it gave, so there is nothing to guess between.
        var output = await BackupCommand("delete", "Ketchup-20260817T023033Z", TwoBackups());

        output.Should().Contain("Staged").And.NotContain("Error");
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("latest")]
    [InlineData("most recent")]
    public async Task ThePositionWords_NameABackupWithNoId(string given)
    {
        // What the request said in the first place. "restore the most recent backup" names a backup
        // without naming an id, and the listing is already newest-first.
        var output = await BackupCommand("restore", given, TwoBackups());

        output.Should().Contain("Staged").And.NotContain("Error");
    }

    [Fact]
    public async Task AMistypedBackupId_IsPointedAtThePositionWords_NotAskedToCopyAgain()
    {
        // A digit changed. Asking for an exact copy is an instruction the model cannot follow — it
        // retries with another corruption until the step budget is gone and nothing is ever staged.
        var output = await BackupCommand("restore", "Ketchup-20260811T023033Z-c6d089", TwoBackups());

        output.Should().StartWith("Error").And.Contain("newest").And.Contain("oldest");

        // And it is never corrected to the nearest real id: restoring the wrong backup over live
        // data is what that would cost.
        output.Should().NotContain("Staged");
    }

    [Fact]
    public async Task AnAmbiguousBackupPrefix_IsAlsoPointedAtThePositionWords()
    {
        var output = await BackupCommand("delete", "Ketchup-2026", TwoBackups());

        output.Should().StartWith("Error").And.Contain("matches 2").And.Contain("newest");
    }

    [Fact]
    public async Task NamingNoBackupAtAll_OffersThePositionWords()
    {
        var output = (await Create(TwoBackups()).ExecuteAsync(new LlmToolCall(
            ShippedText.Name(LlmTools.BackupCommand),
            new Dictionary<string, string?>
            {
                ["instance_name"] = "Ketchup",
                ["verb"] = "delete",
            }))).Summary;

        output.Should().StartWith("Error").And.Contain("newest").And.Contain("oldest");
    }

    [Fact]
    public async Task AnUnreadableListing_DoesNotRefuseAnIdItCannotCheck()
    {
        // The listing failing says nothing about the id. Refusing here would block a correct id on
        // an unrelated outage; the confirm step answers for it instead.
        var output = await BackupCommand("delete", "Ketchup-20260817T023033Z-c6d089", new StubFacts());

        output.Should().Contain("Staged");
    }

    // --- config ---

    [Fact]
    public async Task Config_ReportsTheKeysTheSetterTakes_AndWhichOfThemAreSettable()
    {
        var facts = new StubFacts
        {
            Config = new InstanceConfigFacts(FactsState.Available, new[]
            {
                new InstanceSetting("auto_update", "true", Settable: true),
                new InstanceSetting("backup_retention", "3", Settable: true),
                new InstanceSetting("name", "Ketchup", Settable: false),
                // A path KGSM owns: a location, not a setting, and dropped from the listing.
                new InstanceSetting("install_dir", "/opt/palworld/Ketchup", Settable: false),
            }),
        };

        var output = await Run(LlmTools.GetInstanceConfig, facts);

        output.Should().Contain("auto_update = true")
            .And.Contain("backup_retention = 3")
            .And.Contain("name = Ketchup")
            .And.NotContain("install_dir");

        // The two halves are named apart, so a change is only ever offered on a key that accepts one.
        output.Should().Contain("Managed by KGSM, cannot be changed");
    }

    [Fact]
    public async Task Config_EmptyValueIsAValue_NotAnAbsence()
    {
        var facts = new StubFacts
        {
            Config = new InstanceConfigFacts(FactsState.Available, new[]
            {
                new InstanceSetting("steamcmd_arguments", "", Settable: true),
            }),
        };

        var output = await Run(LlmTools.GetInstanceConfig, facts);

        output.Should().Contain("steamcmd_arguments = (empty)");
    }

    // --- note ---

    [Fact]
    public async Task Note_ReportsTheBodyAndWhoWroteIt()
    {
        var facts = new StubFacts
        {
            Note = new NoteFacts(FactsState.Available, "Modded — read the pins first.", "heisen", "2026-08-01"),
        };

        var output = await Run(LlmTools.GetInstanceNote, facts);

        output.Should().Contain("Modded — read the pins first.")
            .And.Contain("heisen")
            .And.Contain("2026-08-01");
    }

    [Fact]
    public async Task Note_NoneSetIsMeasured_AndUnreadableIsNot()
    {
        var none = await Run(LlmTools.GetInstanceNote,
            new StubFacts { Note = new NoteFacts(FactsState.Available, null, null, null) });
        none.Should().Contain("has no server note set");

        var unreadable = await Run(LlmTools.GetInstanceNote,
            new StubFacts { Note = new NoteFacts(FactsState.Unavailable, null, null, null) });
        unreadable.Should().Contain("isn't the same as it having none");
    }

    // --- host ports and conflicts ---

    [Fact]
    public async Task HostPorts_NameThePortsAndWhichServerOwnsEach()
    {
        _hostFacts.GetPortUsageAsync(Arg.Any<CancellationToken>()).Returns(new HostPortUsage(
            FactsState.Available,
            new[]
            {
                new HostPortEntry(8211, "udp", "PalServer-Linux", "Ketchup"),
                new HostPortEntry(8082, "tcp", "llama-server", null),
                new HostPortEntry(22, "tcp", null, null),
            },
            FactsState.Available,
            []));

        var output = (await Create(new StubFacts()).ExecuteAsync(
            new LlmToolCall(ShippedText.Name(LlmTools.ListHostPorts), new Dictionary<string, string?>()))).Summary;

        output.Should().Contain("8211/udp — Ketchup, held by PalServer-Linux")
            .And.Contain("8082/tcp, held by llama-server")
            // An unattributed socket says so rather than dropping the clause, which would read as
            // nothing holding it.
            .And.Contain("22/tcp, holding process not identified")
            .And.Contain("Configured for a game server (1)")
            .And.Contain("Not configured for any game server (2)");
    }

    [Fact]
    public async Task PortConflicts_NoneIsStatedPlainly_AndAnUnrunScanIsNot()
    {
        _hostFacts.GetPortUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new HostPortUsage(FactsState.Available, [], FactsState.Available, []));

        var clean = (await Create(new StubFacts()).ExecuteAsync(
            new LlmToolCall(ShippedText.Name(LlmTools.FindPortConflicts), new Dictionary<string, string?>()))).Summary;

        clean.Should().Contain("No port conflicts");

        // The failure mode this replaced: a scan that could not run reported as "all clear".
        _hostFacts.GetPortUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new HostPortUsage(FactsState.Available, [], FactsState.Unavailable, []));

        var unread = (await Create(new StubFacts()).ExecuteAsync(
            new LlmToolCall(ShippedText.Name(LlmTools.FindPortConflicts), new Dictionary<string, string?>()))).Summary;

        unread.Should().Contain("didn't run").And.Contain("isn't the same as there being none");
    }

    [Fact]
    public async Task PortConflicts_WordTwoInstancesApartFromAnOutsideProcess()
    {
        _hostFacts.GetPortUsageAsync(Arg.Any<CancellationToken>()).Returns(new HostPortUsage(
            FactsState.Available, [], FactsState.Available,
            new[]
            {
                new PortConflictEntry(27015, "udp", "Ketchup", "romestead", OtherIsInstance: true),
                new PortConflictEntry(25565, "tcp", "minecraft", "java:392616", OtherIsInstance: false),
            }));

        var output = (await Create(new StubFacts()).ExecuteAsync(
            new LlmToolCall(ShippedText.Name(LlmTools.FindPortConflicts), new Dictionary<string, string?>()))).Summary;

        // The two are fixed by opposite actions, so they must not read alike.
        output.Should().Contain("'Ketchup' and 'romestead' are both configured for it")
            .And.Contain("change the port on one of them")
            .And.Contain("java:392616 is already holding it")
            .And.Contain("isn't a KGSM server");
    }

    /// <summary>
    /// A facts port whose every read is unavailable unless the test sets it. Unavailable is the
    /// honest default: a test that forgets to arrange a leg gets "couldn't read", never an
    /// accidental empty world it might then assert on.
    /// </summary>
    private sealed class StubFacts : IServerFacts
    {
        public BackupListing Backups { get; init; } = new(FactsState.Unavailable, []);
        public InstanceConfigFacts Config { get; init; } = new(FactsState.Unavailable, []);
        public NoteFacts Note { get; init; } = new(FactsState.Unavailable, null, null, null);

        public Task<BackupListing> GetBackupsAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(Backups);
        public Task<InstanceConfigFacts> GetConfigAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(Config);
        public Task<NoteFacts> GetNoteAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(Note);
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
