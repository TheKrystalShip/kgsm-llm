using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Fetch;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.RootCause;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the dispatcher's routing, name resolution, and confirmation staging
/// against the host ports (<see cref="IServerOperations"/> / <see cref="IServerInventory"/>),
/// which the host implements over whatever it uses to talk to kgsm.
/// </summary>
public class ToolDispatcherTests
{
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly ISearch _search = Substitute.For<ISearch>();
    private readonly IWebFetch _webFetch = Substitute.For<IWebFetch>();
    private readonly IServerMetrics _metrics = Substitute.For<IServerMetrics>();
    private readonly IEventHistory _events = Substitute.For<IEventHistory>();
    private readonly INetworkInfo _network = Substitute.For<INetworkInfo>();
    private readonly IUpnpInfo _upnp = Substitute.For<IUpnpInfo>();
    private readonly IBlueprintAuthoring _blueprintAuthoring = Substitute.For<IBlueprintAuthoring>();
    private readonly ConfirmationContext _confirmations = new();

    public ToolDispatcherTests()
    {
        // Two terraria-* instances (matched by substring / game type) plus a unique minecraft.
        var instances = new Dictionary<string, string>
        {
            ["terraria-pvp"] = "terraria",
            ["terraria-creative"] = "terraria",
            ["minecraft"] = "minecraft",
        };
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)instances);

        // A catalog carrying both names a blueprint has: the identifier kgsm installs, and the name a
        // person says. `projectzomboid` earns its place by being the pair that differ most.
        var blueprints = new BlueprintSummary[]
        {
            new("valheim", "Valheim"),
            new("terraria", "Terraria"),
            new("projectzomboid", "Project Zomboid"),
        };
        _inventory.GetBlueprintCatalogAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<BlueprintSummary>)blueprints);
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)blueprints.Select(b => b.Name).ToArray());

        // Default the UPnP axis to "watchdog unreachable" so get_network tests that only exercise the
        // firewall axis get a valid (non-null) reading; router-specific tests override this.
        _upnp.GetForwardsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UpnpReading(UpnpState.DaemonUnavailable, Array.Empty<UpnpForward>()));
    }

    /// <summary>
    /// A settlement window short enough that an unsettled auto-run closes in milliseconds. The real
    /// window is 90 seconds, which is right in production and useless in a suite.
    /// </summary>
    // The fail-closed defaults: these tests cover routing and staging, not the facts aspects, so an
    // honest "authority unavailable" is the right stand-in rather than a fabricated reading.
    private readonly IServerFacts _serverFacts = new UnavailableServerFacts();
    private readonly IHostFacts _hostFacts = new UnavailableHostFacts();

    private readonly SettlementTiming _settlement =
        new(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10));

    private ToolDispatcher Create(IServerFacts? serverFacts = null) =>
        new(_operations, _inventory, _confirmations, _search, _webFetch, _metrics, _events, _network, _upnp,
            serverFacts ?? _serverFacts, _hostFacts, _blueprintAuthoring, ShippedText.Catalog, _settlement,
            new InMemoryMemoryStore(), Options.Create(new MemoryOptions()),
            NullLogger<ToolDispatcher>.Instance);

    // Phase 2: ExecuteAsync now returns ToolOutput (model-facing summary + optional surface card). The
    // routing/resolution/staging tests below assert on the model-facing summary, so unwrap it once here.
    private async Task<string> Summary(LlmToolCall call, IServerFacts? serverFacts = null) =>
        (await Create(serverFacts: serverFacts).ExecuteAsync(call)).Summary;

    private static LlmToolCall Call(Tool name, string instance) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = instance });

    private static LlmToolCall ServerCommandCall(string verb, string instance) =>
        new(ShippedText.Name(LlmTools.ServerCommand), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["verb"] = verb,
        });

    private static LlmToolCall InstallCall(string blueprint, string? name = null) =>
        new(ShippedText.Name(LlmTools.InstallServer), new Dictionary<string, string?>
        {
            ["blueprint_name"] = blueprint,
            ["instance_name"] = name,
        });

    private static LlmToolCall SetConfigCall(string instance, string? key, string? value) =>
        new(ShippedText.Name(LlmTools.SetConfigValue), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["config_key"] = key,
            ["config_value"] = value,
        });

    private static LlmToolCall SearchCall(string? query) =>
        new(ShippedText.Name(LlmTools.Search), new Dictionary<string, string?> { ["query"] = query });

    private static LlmToolCall FetchUrlCall(string? url) =>
        new(ShippedText.Name(LlmTools.FetchUrl), new Dictionary<string, string?> { ["url"] = url });

    private static LlmToolCall SetGameSettingCall(
        string instance, string? path, string? setting, string? value, string? copyFrom = null) =>
        new(ShippedText.Name(LlmTools.SetGameSetting), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["path"] = path,
            ["setting"] = setting,
            ["value"] = value,
            ["copy_from"] = copyFrom,
        });

    private static LlmToolCall WriteFileCall(
        string instance, string? path, string? oldText, string? newText, string? copyFrom = null) =>
        new(ShippedText.Name(LlmTools.WriteFile), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["path"] = path,
            ["old_string"] = oldText,
            ["new_string"] = newText,
            ["copy_from"] = copyFrom,
        });

    [Fact]
    public async Task ExactName_Resolves_AndExecutes()
    {
        var facts = Substitute.For<IServerFacts>();

        await Summary(Call(ShippedText.Name(LlmTools.ServerInfo), "minecraft"), serverFacts: facts);

        await facts.Received(1).GetStatusAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleFuzzyMatch_Resolves()
    {
        var facts = Substitute.For<IServerFacts>();

        // "pvp" is a substring of exactly one instance.
        await Summary(Call(ShippedText.Name(LlmTools.ServerInfo), "pvp"), serverFacts: facts);

        await facts.Received(1).GetStatusAsync("terraria-pvp", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousName_AsksUser_AndDoesNotExecute()
    {
        // "terraria" matches two instances by game type / substring.
        var result = await Summary(Call(ShippedText.Name(LlmTools.ServerInfo), "terraria"));

        result.Should().Contain("Ambiguous")
            .And.Contain("terraria-pvp")
            .And.Contain("terraria-creative");
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownName_ReturnsMiss_WithKnownList()
    {
        var result = await Summary(Call(ShippedText.Name(LlmTools.ServerInfo), "doesnotexist"));

        result.Should().Contain("no instance named").And.Contain("minecraft");
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_NoInstanceName_ReturnsFleetSummary_InASingleBulkCall()
    {
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FleetStatusEntry>>(new[]
            {
                new FleetStatusEntry("minecraft", FleetStatusAvailability.Read, true, null),
                new FleetStatusEntry("terraria-pvp", FleetStatusAvailability.Read, false, null),
            }));

        var result = await Summary(
            new LlmToolCall(ShippedText.Name(LlmTools.ServerInfo), new Dictionary<string, string?>()));

        result.Should().Contain("minecraft: running").And.Contain("terraria-pvp: stopped");

        // The MaxIterations fix: one bulk call, never a per-instance fan-out.
        await _operations.Received(1).GetFleetStatusAsync(Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_Fleet_SurfacesStructuredCard_WithNeverCollapsedUnknown()
    {
        // Phase 2 §5·b: fleet mode (no instance_name) surfaces a FleetStatusData card on
        // ToolOutput.Data — while the model still gets only the Summary string. The single-server
        // mode stays cardless (opaque kgsm string, no structured source — asserted by its absence).
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FleetStatusEntry>>(new[]
            {
                new FleetStatusEntry("minecraft", FleetStatusAvailability.Read, true, null),
                new FleetStatusEntry("broken", FleetStatusAvailability.Unavailable, null, "needs regeneration"),
            }));

        var output = await Create().ExecuteAsync(
            new LlmToolCall(ShippedText.Name(LlmTools.ServerInfo), new Dictionary<string, string?>()));

        output.Summary.Should().Contain("minecraft: running").And.Contain("broken: status unavailable");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.Status.Name);
        card.Subject.Should().Be(new ResultRef(ResourceKind.Host, "primary"));
        var data = card.Data.Should().BeOfType<FleetStatusData>().Subject;
        data.Running.Should().Be(1);
        data.Unavailable.Should().Be(1);
        // Measured-or-unknown carried into the card: the unreadable instance is Unknown, never Stopped.
        data.Servers.Single(s => s.Instance == "broken").State.Should().Be(ServerRunState.Unknown);
        data.Stopped.Should().Be(0);
    }

    [Fact]
    public async Task GetStatus_SingleServer_ReportsTheMeasuredState()
    {
        var output = await Create(serverFacts: FactsWithPorts("25565/tcp"))
            .ExecuteAsync(Call(ShippedText.Name(LlmTools.ServerInfo), "minecraft"));

        // Rendered from the engine's structured read, not relayed as its report text: the run state
        // is a word rather than a bare boolean, and the version comparison is spelled out.
        output.Summary.Should().Contain("minecraft is RUNNING")
            .And.Contain("Version 1.21")
            .And.Contain("up to date");
    }

    [Fact]
    public async Task GetStatus_Fleet_UnreadableInstance_IsUnavailable_NeverStopped()
    {
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FleetStatusEntry>>(new[]
            {
                new FleetStatusEntry("broken", FleetStatusAvailability.Unavailable, null,
                    "its management file must be regenerated to report status"),
            }));

        var result = await Summary(
            new LlmToolCall(ShippedText.Name(LlmTools.ServerInfo), new Dictionary<string, string?>()));

        // Measured-or-unknown: a could-not-read instance must not masquerade as stopped.
        result.Should().Contain("status unavailable").And.Contain("regenerated");
        result.Should().NotContain("stopped");
    }

    // --- run_health_check (the first aggregator) ---

    [Fact]
    public async Task RunHealthCheck_Resolves_FetchesSnapshot_ReturnsSummary()
    {
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new InstanceHealthSnapshot(
                Running: true,
                RecentLogLines: Array.Empty<string>(),
                RecentLogLinesRequested: 200,
                UpdatesAvailable: false,
                CurrentVersion: "1.0.0",
                LatestVersion: null,
                HostDisk: new HostDisk(26, "916G", "649G"),
                HostDiskUnavailableReason: null)));

        var result = await Summary(Call(ShippedText.Name(LlmTools.RunHealthCheck), "minecraft"));

        // The dispatcher returns the aggregator's deterministic summary (the model's grounding text).
        result.Should().Contain("minecraft").And.Contain("healthy");
        await _operations.Received(1).GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHealthCheck_UnresolvedInstance_DoesNotFetch()
    {
        var result = await Summary(Call(ShippedText.Name(LlmTools.RunHealthCheck), "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHealthCheck_PortFailure_ReturnsError()
    {
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<InstanceHealthSnapshot>("kgsm unreachable"));

        var result = await Summary(Call(ShippedText.Name(LlmTools.RunHealthCheck), "minecraft"));

        result.Should().Contain("could not run a health check").And.Contain("kgsm unreachable");
    }

    [Fact]
    public async Task RunHealthCheck_SurfacesStructuredCard_AlongsideSummary()
    {
        // Phase 2 (§5·c): run_health_check is the one tool with a real card today — it surfaces the
        // deterministic HealthData on ToolOutput.Data for a streaming surface, while the model still
        // gets ONLY the Summary string (the assertion above). An available update makes Overall = Warn.
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new InstanceHealthSnapshot(
                Running: true,
                RecentLogLines: new[] { "INFO all good" },
                RecentLogLinesRequested: 200,
                UpdatesAvailable: true,
                CurrentVersion: "1.0.0",
                LatestVersion: "1.1.0",
                HostDisk: new HostDisk(26, "916G", "649G"),
                HostDiskUnavailableReason: null)));

        var output = await Create().ExecuteAsync(Call(ShippedText.Name(LlmTools.RunHealthCheck), "minecraft"));

        output.Summary.Should().Contain("minecraft");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.Health.Name);
        card.Confidence.Should().Be(Confidence.Confirmed);          // a deterministic read of measured facts
        card.Subject.Should().Be(new ResultRef(ResourceKind.Server, "minecraft"));
        var data = card.Data.Should().BeOfType<HealthData>().Subject;
        data.Overall.Should().Be(CheckState.Warn);                  // the update check warns → worst non-skip
        data.Checks.Should().Contain(c => c.Name == "updates" && c.State == CheckState.Warn);
    }

    [Fact]
    public async Task ReadFile_NoPath_DefaultsToConfig_AndReturnsContentVerbatim()
    {
        // An omitted path falls back to <name>.config.ini (the old view_config_file affordance),
        // and content is returned verbatim — no redaction (owner decision).
        _operations.ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>())
            .Returns(Result.Success("port = 25565\nrcon_password = hunter2\nlevel = world"));

        var result = await Summary(Call(ShippedText.Name(LlmTools.ReadFile), "minecraft"));

        // The default filename is derived from the resolved instance name (no model-supplied path).
        await _operations.Received(1)
            .ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>());

        result.Should().Contain("port = 25565").And.Contain("level = world");
        result.Should().Contain("rcon_password = hunter2"); // verbatim — redaction was dropped
    }

    [Fact]
    public async Task ReadFile_ExplicitPath_ReadsThatFileWithinTheInstance()
    {
        _operations.ReadInstanceFileAsync("minecraft", "logs/latest.log", Arg.Any<CancellationToken>())
            .Returns(Result.Success("[12:00] server started"));

        var call = new LlmToolCall(ShippedText.Name(LlmTools.ReadFile), new Dictionary<string, string?>
        {
            ["instance_name"] = "minecraft",
            ["path"] = "logs/latest.log",
        });
        var result = await Summary(call);

        await _operations.Received(1)
            .ReadInstanceFileAsync("minecraft", "logs/latest.log", Arg.Any<CancellationToken>());
        result.Should().Contain("logs/latest.log").And.Contain("server started");
    }

    [Fact]
    public async Task ReadFile_UnknownInstance_DoesNotRead()
    {
        var result = await Summary(Call(ShippedText.Name(LlmTools.ReadFile), "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .ReadInstanceFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListFiles_FormatsEntries_DirsFlaggedAndFilesSized()
    {
        _operations.ListInstanceDirectoryAsync("minecraft", null, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<InstanceDirEntry>>(new[]
            {
                new InstanceDirEntry("logs", IsDirectory: true, Size: 0),
                new InstanceDirEntry("minecraft.config.ini", IsDirectory: false, Size: 2048),
            }));

        var result = await Summary(Call(ShippedText.Name(LlmTools.ListFiles), "minecraft"));

        await _operations.Received(1)
            .ListInstanceDirectoryAsync("minecraft", null, Arg.Any<CancellationToken>());
        result.Should().Contain("logs/").And.Contain("minecraft.config.ini").And.Contain("KB");
    }

    [Fact]
    public async Task ListFiles_WithSubdir_PassesItThrough()
    {
        _operations.ListInstanceDirectoryAsync("minecraft", "logs", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<InstanceDirEntry>>(
                new[] { new InstanceDirEntry("latest.log", IsDirectory: false, Size: 10) }));

        var call = new LlmToolCall(ShippedText.Name(LlmTools.ListFiles), new Dictionary<string, string?>
        {
            ["instance_name"] = "minecraft",
            ["subdir"] = "logs",
        });
        var result = await Summary(call);

        await _operations.Received(1)
            .ListInstanceDirectoryAsync("minecraft", "logs", Arg.Any<CancellationToken>());
        result.Should().Contain("minecraft/logs").And.Contain("latest.log");
    }

    [Fact]
    public async Task ListFiles_UnknownInstance_DoesNotList()
    {
        var result = await Summary(Call(ShippedText.Name(LlmTools.ListFiles), "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .ListInstanceDirectoryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Summary(
            new LlmToolCall(new Tool("delete_everything"), new Dictionary<string, string?>()));

        result.Should().Contain("no tool called");
    }

    // --- search (unified lookup via the ISearch aggregator) ---
    // The dispatcher only guards the blank query and relays the aggregator's grounding text verbatim;
    // the local-first / web-fallback composition is exercised in SearchAggregatorTests.

    private static ToolResult<SearchData> SearchEnvelope(string summary, SearchState state, params SearchPassage[] passages) =>
        new(ResultCardKinds.Search, Confidence.Likely, new ResultRef(ResourceKind.Search, "q"), summary, new SearchData("q", state, passages));

    [Fact]
    public async Task Search_RelaysTheAggregatorGroundingVerbatim()
    {
        _search.SearchAsync("terraria latest version", Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("Web results for \"terraria latest version\" … source: https://terraria.org/news",
                SearchState.Web, new SearchPassage(SearchProvenance.Web, "https://terraria.org/news", "News", "…", 0.9)));

        var result = await Summary(SearchCall("terraria latest version"));

        result.Should().Be("Web results for \"terraria latest version\" … source: https://terraria.org/news");
        await _search.Received(1).SearchAsync("terraria latest version", Arg.Any<SearchScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_WithPassages_SurfacesTheSearchCard()
    {
        _search.SearchAsync("what is kgsm", Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("From the operator's indexed docs …", SearchState.LocalStrong,
                new SearchPassage(SearchProvenance.Local, "docs/x.md", "X > Y", "KGSM …", 0.8)));

        var output = await Create().ExecuteAsync(SearchCall("what is kgsm"));

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.Search.Name);   // the card carries the tool NAME ("search")
        card.Data.Should().BeOfType<SearchData>().Which.State.Should().Be(SearchState.LocalStrong);
    }

    [Fact]
    public async Task Search_WithNoPassages_StaysSummaryOnly()
    {
        _search.SearchAsync("nonexistent", Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("No results …", SearchState.Empty));

        var output = await Create().ExecuteAsync(SearchCall("nonexistent"));

        output.Summary.Should().Be("No results …");
        output.Data.Should().BeNull();   // nothing to cite → no card
    }

    [Fact]
    public async Task Search_BlankQuery_DoesNotCallTheAggregator()
    {
        var result = await Summary(SearchCall("   "));

        result.Should().Contain("needs a 'query'");
        await _search.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<SearchScope>(), Arg.Any<CancellationToken>());
    }

    // --- fetch_url (reading ONE specific page via the IWebFetch port) ---
    // The dispatcher only guards the blank url and relays the adapter's text/outcome honestly; the
    // SSRF guard / redirect handling / content extraction are exercised in HttpWebFetchTests.

    [Fact]
    public async Task FetchUrl_RelaysThePageTextInTheSummary()
    {
        _webFetch.FetchAsync("https://docs.example.com/setup", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult(
                "https://docs.example.com/setup", 200, "text/html", "Setup Guide", "Run the server binary.", Truncated: false)));

        var result = await Summary(FetchUrlCall("https://docs.example.com/setup"));

        result.Should().Contain("https://docs.example.com/setup");
        result.Should().Contain("Run the server binary.");
        await _webFetch.Received(1).FetchAsync("https://docs.example.com/setup", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchUrl_OnSuccess_SurfacesTheFetchCard()
    {
        _webFetch.FetchAsync("https://docs.example.com/setup", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult(
                "https://docs.example.com/setup", 200, "text/html", "Setup Guide", "Run the server binary.", Truncated: false)));

        var output = await Create().ExecuteAsync(FetchUrlCall("https://docs.example.com/setup"));

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.WebPage.Name);
        var data = card.Data.Should().BeOfType<FetchData>().Subject;
        data.Url.Should().Be("https://docs.example.com/setup");
        data.Title.Should().Be("Setup Guide");
        data.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task FetchUrl_FailedFetch_IsHonestAndStaysSummaryOnly()
    {
        _webFetch.FetchAsync("https://blocked.internal/", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<WebFetchResult>("refused to fetch this address ('blocked.internal' is a private/internal address)"));

        var output = await Create().ExecuteAsync(FetchUrlCall("https://blocked.internal/"));

        output.Summary.Should().Contain("Couldn't fetch");
        output.Summary.Should().NotContain("empty", "a failure must never read as 'the page is empty'");
        output.Data.Should().BeNull();
    }

    [Fact]
    public async Task FetchUrl_TruncatedPage_NotesItInTheSummary()
    {
        _webFetch.FetchAsync("https://big.example.com/", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult(
                "https://big.example.com/", 200, "text/plain", null, "partial content", Truncated: true)));

        var result = await Summary(FetchUrlCall("https://big.example.com/"));

        result.Should().Contain("truncated");
    }

    [Fact]
    public async Task FetchUrl_BlankUrl_DoesNotCallTheAdapter()
    {
        var result = await Summary(FetchUrlCall("   "));

        result.Should().Contain("needs a 'url'");
        await _webFetch.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- create_blueprint (the authoring pipeline via the IBlueprintAuthoring port) ---
    // The dispatcher only guards the blank game arg and relays the port's outcome; the whole pipeline
    // (research/draft/persist/install/verify/teardown) is exercised in BlueprintAuthoringAggregatorTests.

    private static LlmToolCall CreateBlueprintCall(string? game) =>
        new(ShippedText.Name(LlmTools.CreateBlueprint), new Dictionary<string, string?> { ["game"] = game });

    [Fact]
    public async Task CreateBlueprint_DraftReady_GroundsTheModelToAskForReview_NotToClaimItsAdded()
    {
        // The mandatory-review flow: create_blueprint drafts only, and the tool's grounding text must tell
        // the model NOT to claim the game is added — the test-install runs later, on the user's save.
        _blueprintAuthoring.DraftAsync("Terraria", Arg.Any<CancellationToken>()).Returns(DraftReadyEnvelope());

        var result = await Summary(CreateBlueprintCall("Terraria"));

        result.Should().MatchRegex("(?i)review|save");
        result.Should().NotContain("is now in the catalog");
        await _blueprintAuthoring.Received(1).DraftAsync("Terraria", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateBlueprint_DraftReady_StagesABlueprintConfirmation_CarryingTheDraftYaml()
    {
        using var scope = _confirmations.BeginTurn();
        _blueprintAuthoring.DraftAsync("Terraria", Arg.Any<CancellationToken>()).Returns(DraftReadyEnvelope());

        var output = await Create().ExecuteAsync(CreateBlueprintCall("Terraria"));

        // Card carries the editable draft, and a Blueprint confirmation was staged with the draft YAML body.
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        var data = card.Data.Should().BeOfType<BlueprintAuthoringData>().Subject;
        data.Outcome.Should().Be(BlueprintAuthoringOutcome.DraftReady);
        data.Editable.Should().BeTrue();
        data.DraftYaml.Should().Contain("executable_file");

        var staged = scope.Staged.Should().ContainSingle().Subject;
        staged.Kind.Should().Be(ConfirmationKind.Blueprint);
        staged.Target.Should().Be("terraria");        // slug
        staged.InstanceName.Should().Be("Terraria");  // display name for finalize
        staged.ConfigValue.Should().Contain("executable_file"); // draft YAML fallback body
    }

    [Fact]
    public async Task CreateBlueprint_TerminalOutcome_SurfacesTheCard_AndStagesNothing()
    {
        using var scope = _confirmations.BeginTurn();
        var envelope = new ToolResult<BlueprintAuthoringData>(
            ShippedText.Name(LlmTools.CreateBlueprint), Confidence.Likely, new ResultRef(ResourceKind.Blueprint, "SomeGame"),
            "I couldn't find a native Linux server for it.",
            new BlueprintAuthoringData(BlueprintAuthoringOutcome.NotFeasible, "SomeGame", null, [], null, "I couldn't find a native Linux server for it.", false));
        _blueprintAuthoring.DraftAsync("SomeGame", Arg.Any<CancellationToken>()).Returns(envelope);

        var output = await Create().ExecuteAsync(CreateBlueprintCall("SomeGame"));

        output.Summary.Should().Contain("native Linux server");
        output.Data.Should().BeOfType<ToolResultCard>();
        scope.Staged.Should().BeEmpty(); // a terminal outcome stages no confirmation
    }

    [Fact]
    public async Task CreateBlueprint_BlankGame_DoesNotCallThePort()
    {
        var result = await Summary(CreateBlueprintCall("   "));

        result.Should().Contain("needs a 'game'");
        await _blueprintAuthoring.DidNotReceive().DraftAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static ToolResult<BlueprintAuthoringData> DraftReadyEnvelope() =>
        new(ShippedText.Name(LlmTools.CreateBlueprint), Confidence.Likely, new ResultRef(ResourceKind.Blueprint, "terraria"),
            "I drafted a starting config for Terraria — review and save it to test-run it.",
            new BlueprintAuthoringData(
                BlueprintAuthoringOutcome.DraftReady, "Terraria", "terraria", [], null, null, false,
                DraftYaml: "name: terraria\nruntime: native\nnative:\n  executable_file: TerrariaServer.bin.x86_64\n",
                Evidence: null, Editable: true));

    // --- get_performance (live per-server metrics via the IServerMetrics port) ---
    // The dispatcher resolves the instance, reads the neutral snapshot, runs the pure aggregator, and
    // attaches a card ONLY for a Live read; the Live/NotRunning/Unavailable wording lives in PerformanceReportTests.

    private static LlmToolCall PerformanceCall(string instance) => Call(ShippedText.Name(LlmTools.GetPerformance), instance);

    [Fact]
    public async Task GetPerformance_Live_SurfacesTheMetricsCard()
    {
        _metrics.GetSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsReading(
                PerformanceState.Live, CpuPctCore: 42.5, MemBytes: 1_073_741_824,
                RxBps: 1024, TxBps: 2048, IoReadBps: 0, IoWriteBps: 512, DiskBytes: 5_000_000, Pids: 7));

        var output = await Create().ExecuteAsync(PerformanceCall("minecraft"));

        await _metrics.Received(1).GetSnapshotAsync("minecraft", Arg.Any<CancellationToken>());
        output.Summary.Should().Contain("minecraft").And.Contain("42.5%");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.Performance.Name);   // the card carries the tool NAME ("get_performance")
        card.Confidence.Should().Be(Confidence.Confirmed);
        card.Subject.Should().Be(new ResultRef(ResourceKind.Metrics, "minecraft"));
        var data = card.Data.Should().BeOfType<PerformanceData>().Subject;
        data.State.Should().Be(PerformanceState.Live);
        data.Instance.Should().Be("minecraft");
        data.CpuPctCore.Should().Be(42.5);
        data.Pids.Should().Be(7);
    }

    [Fact]
    public async Task GetPerformance_NotRunning_StaysSummaryOnly()
    {
        _metrics.GetSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsReading(
                PerformanceState.NotRunning, null, null, null, null, null, null, null, null));

        var output = await Create().ExecuteAsync(PerformanceCall("minecraft"));

        output.Summary.Should().Contain("isn't running");
        output.Data.Should().BeNull();   // no live frame → no card
    }

    [Fact]
    public async Task GetPerformance_MonitorUnavailable_StaysSummaryOnly()
    {
        _metrics.GetSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsReading(
                PerformanceState.MonitorUnavailable, null, null, null, null, null, null, null, null));

        var output = await Create().ExecuteAsync(PerformanceCall("minecraft"));

        output.Summary.Should().Contain("unavailable").And.Contain("isn't a sign the server is idle");
        output.Data.Should().BeNull();   // couldn't read → no card
    }

    [Fact]
    public async Task GetPerformance_UnresolvedInstance_DoesNotRead()
    {
        var result = await Summary(PerformanceCall("doesnotexist"));

        result.Should().Contain("no instance named");
        await _metrics.DidNotReceive().GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- the network axes, inside the status read ---
    // There is no separate reachability tool: the status read fetches the configured ports from the
    // engine, what the host firewall has open, and what the router forwards, all concurrently, and
    // reports them together. The wording of each axis lives in NetworkReportTests.

    private static LlmToolCall StatusCall(string instance) =>
        Call(ShippedText.Name(LlmTools.ServerInfo), instance);

    /// <summary>A status reading with ports configured — what makes the network axes reportable.</summary>
    private static IServerFacts FactsWithPorts(params string[] ports) =>
        new StubStatusFacts(new InstanceStatusFacts(
            FactsState.Available, Running: true, Pid: 42, StartedAt: null,
            Blueprint: "minecraft.bp.yaml", Runtime: "native", Directory: "/opt/minecraft",
            DiskUsage: "2.0G", Ports: ports, InstalledVersion: "1.21",
            LatestVersion: "1.21", UpdateAvailable: false, BackupCount: 0));

    private sealed record StubStatusFacts(InstanceStatusFacts Status) : IServerFacts
    {
        public Task<InstanceStatusFacts> GetStatusAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(Status);
        public Task<BackupListing> GetBackupsAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new BackupListing(FactsState.Unavailable, []));
        public Task<VersionFacts> GetVersionAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new VersionFacts(FactsState.Unavailable, null, null, null));
        public Task<InstanceConfigFacts> GetConfigAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new InstanceConfigFacts(FactsState.Unavailable, []));
        public Task<NoteFacts> GetNoteAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new NoteFacts(FactsState.Unavailable, null, null, null));
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

    [Fact]
    public async Task Status_ReportsPortsFirewallAndRouterInOneCall()
    {
        _network.GetPortsAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new NetworkReading(
                NetworkState.Available, "ufw", PortListState.Enumerated, NetworkEnforcement.Enforcing,
                new[] { new PortRule(25565, 25565, "tcp") }));
        _upnp.GetForwardsAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new UpnpReading(UpnpState.Queried, new[] { new UpnpForward(25565, "tcp", 25565, "192.168.1.5") }));

        var output = await Create(serverFacts: FactsWithPorts("25565/tcp"))
            .ExecuteAsync(StatusCall("minecraft"));

        await _network.Received(1).GetPortsAsync("minecraft", Arg.Any<CancellationToken>());
        await _upnp.Received(1).GetForwardsAsync("minecraft", Arg.Any<CancellationToken>());

        // The run state, the configured port, what the firewall has open, and what the router
        // forwards — all from the one read.
        output.Summary.Should().Contain("RUNNING")
            .And.Contain("Configured ports: 25565/tcp")
            .And.Contain("25565/tcp")
            .And.Contain("router");

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.Network.Name);
        var data = card.Data.Should().BeOfType<NetworkData>().Subject;
        data.Backend.Should().Be("ufw");
        data.Ports.Should().ContainSingle();
        data.UpnpState.Should().Be(UpnpState.Queried);
    }

    [Fact]
    public async Task Status_FirewallUnavailable_StillReportsTheRestAndSaysSo()
    {
        _network.GetPortsAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new NetworkReading(
                NetworkState.FirewallUnavailable, "", PortListState.Unknown, NetworkEnforcement.Unknown,
                Array.Empty<PortRule>()));
        _upnp.GetForwardsAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(new UpnpReading(UpnpState.Queried, Array.Empty<UpnpForward>()));

        var output = await Create(serverFacts: FactsWithPorts("25565/tcp"))
            .ExecuteAsync(StatusCall("minecraft"));

        // One axis failing costs that axis and nothing else, and it is stated rather than dropped —
        // a missing line would read as "nothing is open".
        output.Summary.Should().Contain("RUNNING").And.Contain("unavailable");
        output.Data.Should().BeOfType<ToolResultCard>();
    }

    [Fact]
    public async Task Status_WithNoPortsConfigured_SaysSoAndCardsNothing()
    {
        var output = await Create(serverFacts: FactsWithPorts())
            .ExecuteAsync(StatusCall("minecraft"));

        output.Summary.Should().Contain("No ports are configured");
        output.Data.Should().BeNull();
    }

    /// <summary>
    /// The engine answered, and nothing in that answer measured whether the server is up. The one
    /// thing the report must not do is round that to "stopped": an operator reading it would go
    /// looking for a crash that never happened.
    /// </summary>
    [Fact]
    public async Task Status_UnmeasuredRunState_IsUnknownRatherThanStopped()
    {
        var facts = new StubStatusFacts(new InstanceStatusFacts(
            FactsState.Available, Running: null, Pid: null, StartedAt: null,
            Blueprint: "minecraft.bp.yaml", Runtime: null, Directory: "/mnt/vault/minecraft",
            DiskUsage: null, Ports: [], InstalledVersion: null,
            LatestVersion: null, UpdateAvailable: null, BackupCount: 0,
            LibraryState: ServerLibraryState.Offline));

        var output = await Create(serverFacts: facts).ExecuteAsync(StatusCall("minecraft"));

        output.Summary.Should().Contain("UNKNOWN")
            .And.Contain("disk is not mounted")
            .And.Contain("NOT stopped");
        output.Summary.Should().NotContain("STOPPED.");
    }

    /// <summary>
    /// Everything an offline instance would otherwise report is a default nothing read. The report
    /// states that they are unknown instead of printing them, because "No backups." and "no ports
    /// configured" are measurements, and neither was made.
    /// </summary>
    [Fact]
    public async Task Status_OfflineLibrary_StatesNoDefaultsAsFacts()
    {
        var facts = new StubStatusFacts(new InstanceStatusFacts(
            FactsState.Available, Running: null, Pid: null, StartedAt: null,
            Blueprint: "minecraft.bp.yaml", Runtime: null, Directory: "/mnt/vault/minecraft",
            DiskUsage: null, Ports: [], InstalledVersion: null,
            LatestVersion: null, UpdateAvailable: null, BackupCount: 0,
            LibraryState: ServerLibraryState.Offline));

        var output = await Create(serverFacts: facts).ExecuteAsync(StatusCall("minecraft"));

        output.Summary.Should().NotContain("No backups");
        output.Summary.Should().NotContain("No ports are configured");
        output.Summary.Should().Contain("unreadable").And.Contain("None of them are zero");
    }

    [Fact]
    public async Task Status_EngineUnavailable_IsNotReportedAsStopped()
    {
        var output = await Create().ExecuteAsync(StatusCall("minecraft"));

        output.Summary.Should().Contain("didn't answer")
            .And.Contain("isn't the same as it being stopped");
    }

    [Fact]
    public async Task Status_UnresolvedInstance_DoesNotRead()
    {
        var result = await Summary(StatusCall("doesnotexist"));

        result.Should().Contain("no instance named");
        await _network.DidNotReceive().GetPortsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _upnp.DidNotReceive().GetForwardsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- get_audit_log / get_change_timeline (engine event history via the IEventHistory port) ---
    // The dispatcher resolves an OPTIONAL instance (blank → fleet-wide), maps window/range to a
    // `since` bound, reads the raw rows, and runs the pure AuditReport composer — always attaching a
    // card (an empty/unavailable result is still a real, honestly-worded answer worth showing).

    private static LlmToolCall AuditLogCall(string? instance = null, string? window = null) =>
        new(ShippedText.Name(LlmTools.Events), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["window"] = window,
        });

    /// <summary>
    /// The change-scoped read. One <c>events</c> tool serves both scopes, so the narrowing that used
    /// to be a separate tool is now this argument — which is exactly what these tests must exercise.
    /// </summary>
    private static LlmToolCall ChangeTimelineCall(string? instance = null, string? window = null) =>
        new(ShippedText.Name(LlmTools.Events), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["scope"] = "changes",
            ["window"] = window,
        });

    private static readonly AuditEventRow SampleStart =
        new("evt_1", DateTimeOffset.UtcNow, "instance_started", "minecraft", "discord:tester", "assistant");

    [Fact]
    public async Task GetAuditLog_NoInstance_ReadsFleetWide_AndSurfacesCard()
    {
        _events.GetEventsAsync(null, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, new[] { SampleStart }));

        var output = await Create().ExecuteAsync(AuditLogCall());

        await _events.Received(1).GetEventsAsync(null, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        output.Summary.Should().Contain("all servers");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.AuditLog.Name);
        var data = card.Data.Should().BeOfType<AuditData>().Subject;
        data.Instance.Should().BeNull();
        data.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAuditLog_WithInstance_ResolvesFirst_ThenScopesTheRead()
    {
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, new[] { SampleStart }));

        var output = await Create().ExecuteAsync(AuditLogCall("minecraft", "1h"));

        await _events.Received(1).GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        output.Summary.Should().Contain("minecraft").And.Contain("last 1h");
    }

    [Fact]
    public async Task GetAuditLog_UnresolvedInstance_DoesNotRead()
    {
        var output = await Create().ExecuteAsync(AuditLogCall("doesnotexist"));

        output.Summary.Should().Contain("no instance named");
        await _events.DidNotReceive().GetEventsAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAuditLog_MonitorUnavailable_StillSurfacesACard_WordedHonestly()
    {
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.JournalUnavailable, Array.Empty<AuditEventRow>()));

        var output = await Create().ExecuteAsync(AuditLogCall("minecraft"));

        output.Summary.Should().Contain("unavailable");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Confidence.Should().Be(Confidence.Possible);
    }

    [Fact]
    public async Task GetAuditLog_UnknownWindow_FallsBackToDefault_NeverErrors()
    {
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, Array.Empty<AuditEventRow>()));

        var output = await Create().ExecuteAsync(AuditLogCall("minecraft", "nonsense"));

        output.Summary.Should().Contain("last 24h");   // the tool's default window
    }

    [Fact]
    public async Task TraceRootCause_FetchesAllThreeSources_AndSurfacesCard()
    {
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, Array.Empty<AuditEventRow>()));
        _metrics.GetHistoryAsync("minecraft", "24h", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsHistory(PerformanceState.MonitorUnavailable, "24h", null,
                new Dictionary<string, IReadOnlyList<MetricPoint>>()));
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new InstanceHealthSnapshot(
                true, new[] { "INFO ok" }, 200, false, "1.0.0", null, new HostDisk(20, "100G", "80G"), null)));

        var output = await Create().ExecuteAsync(RootCauseCall("minecraft"));

        await _events.Received(1).GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _metrics.Received(1).GetHistoryAsync("minecraft", "24h", Arg.Any<CancellationToken>());
        await _operations.Received(1).GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>());
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(ResultCardKinds.RootCause.Name);
        var data = card.Data.Should().BeOfType<RootCauseData>().Subject;
        data.Instance.Should().Be("minecraft");
        data.Findings.Should().NotBeEmpty();
    }


    // --- diagnose_instance_failure (the capstone aggregator: fans out to _events + _metrics + _operations) ---
    // Rule-matching itself is covered exhaustively by RootCauseAggregatorTests (pure, no mocks); these
    // pin the DISPATCHER's job — resolve the REQUIRED instance, fetch all three sources, and always
    // surface a card.

    private static LlmToolCall RootCauseCall(string instance, string? range = null) =>
        new(ShippedText.Name(LlmTools.TraceRootCause), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["range"] = range,
        });

    [Fact]
    public async Task TraceRootCause_UnresolvedInstance_DoesNotReadAnySource()
    {
        var output = await Create().ExecuteAsync(RootCauseCall("doesnotexist"));

        output.Summary.Should().Contain("no instance named");
        await _events.DidNotReceive().GetEventsAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _metrics.DidNotReceive().GetHistoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TraceRootCause_NoInstanceName_RefusesBeforeFetching()
    {
        // Unlike get_audit_log/get_change_timeline, instance_name is REQUIRED here — root cause is
        // always about one server.
        var output = await Create().ExecuteAsync(RootCauseCall(""));

        output.Summary.Should().Contain("no instance_name was provided");
        await _events.DidNotReceive().GetEventsAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TraceRootCause_HealthReadFails_StillSurfacesACard_WithSourceMarkedUnavailable()
    {
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, Array.Empty<AuditEventRow>()));
        _metrics.GetHistoryAsync("minecraft", "24h", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsHistory(PerformanceState.MonitorUnavailable, "24h", null,
                new Dictionary<string, IReadOnlyList<MetricPoint>>()));
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<InstanceHealthSnapshot>("kgsm unreachable"));

        var output = await Create().ExecuteAsync(RootCauseCall("minecraft"));

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        var data = card.Data.Should().BeOfType<RootCauseData>().Subject;
        data.HealthAvailable.Should().BeFalse();
        data.HealthUnavailableReason.Should().Be("kgsm unreachable");
    }

    public static IEnumerable<object[]> StagedCommandCases() => new[]
    {
        new object[] { "start", ConfirmationKind.Start },
        new object[] { "stop", ConfirmationKind.Stop },
        new object[] { "restart", ConfirmationKind.Restart },
        new object[] { "backup", ConfirmationKind.Backup },
        new object[] { "update", ConfirmationKind.Update },
    };

    [Theory]
    [MemberData(nameof(StagedCommandCases))]
    public async Task ServerCommand_StagesConfirmation_AndDoesNotExecuteInline(string verb, ConfirmationKind kind)
    {
        // server_command routes its `verb` to the matching kind and STAGES it; the
        // single-instance op runs later (from ConfirmAsync), never inline.
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Summary(ServerCommandCall(verb, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(kind, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");

        // None of the mutating ops fired inline — staging only.
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateBackupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(StagedCommandCases))]
    public async Task ServerCommand_AutoExecute_RunsImmediately_AndStagesNothing(string verb, ConfirmationKind kind)
    {
        // Auto-accept turn (admin + toggle, decided by the api): the lifecycle verb RUNS now via the
        // matching IServerOperations op and reports the outcome — nothing is staged for confirmation.
        StubOp(kind, "minecraft", Result.Success());
        StubFleet("minecraft", CommandSettlement.ExpectedRunning(kind) ?? true);

        string result;
        using (_confirmations.BeginTurn(autoExecute: true))
        {
            result = await Summary(ServerCommandCall(verb, "minecraft"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Done").And.NotContain("awaiting");
        await ReceivedOp(kind, "minecraft");
    }

    [Fact]
    public async Task ServerCommand_AutoExecute_NeverReachesRunning_TellsTheModelItDidNot()
    {
        // The model reads this string and repeats it to the user. A start the engine accepted but that
        // never came up must not read as "Done" — that is how a user is told a server is running when
        // it is not.
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        StubFleet("minecraft", running: false);

        string result;
        using (_confirmations.BeginTurn(autoExecute: true))
            result = await Summary(ServerCommandCall("start", "minecraft"));

        result.Should().NotContain("Done");
        result.Should().Contain("still stopped");
    }

    [Fact]
    public async Task ServerCommand_AutoExecute_StateUnreadable_TellsTheModelItIsUnknown()
    {
        StubOp(ConfirmationKind.Start, "minecraft", Result.Success());
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<IReadOnlyList<FleetStatusEntry>>(
                [new FleetStatusEntry("minecraft", FleetStatusAvailability.Unavailable, null, "the status source is offline")])));

        string result;
        using (_confirmations.BeginTurn(autoExecute: true))
            result = await Summary(ServerCommandCall("start", "minecraft"));

        result.Should().NotContain("Done");
        result.Should().Contain("unknown");
    }

    [Fact]
    public async Task ServerCommand_AutoExecute_OpFailure_ReportsError()
    {
        // A failed op surfaces the kgsm error to the model (so it can tell the user honestly), and
        // still stages nothing.
        _operations.StartAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("kgsm exploded")));

        string result;
        using (_confirmations.BeginTurn(autoExecute: true))
        {
            result = await Summary(ServerCommandCall("start", "minecraft"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Could not start").And.Contain("kgsm exploded");
    }

    [Fact]
    public async Task UninstallServer_AutoExecute_StillStages_NotLifecycle()
    {
        // "Lifecycle-only": even on an auto-accept turn, uninstall (and install / set-config) stay
        // propose-only — they keep their own stage methods and never auto-run.
        string result;
        using (_confirmations.BeginTurn(autoExecute: true))
        {
            result = await Summary(Call(ShippedText.Name(LlmTools.UninstallServer), "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
        await _operations.DidNotReceive().UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Stubs the run-state read the auto-run path settles against. An auto-run reports to the MODEL,
    /// so an unobserved outcome would become the model telling the user the server is up.
    /// </summary>
    private void StubFleet(string instance, bool running) =>
        _operations.GetFleetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<IReadOnlyList<FleetStatusEntry>>(
                [new FleetStatusEntry(instance, FleetStatusAvailability.Read, running, null)])));

    private void StubOp(ConfirmationKind kind, string instance, Result result)
    {
        var task = Task.FromResult(result);
        switch (kind)
        {
            case ConfirmationKind.Start: _operations.StartAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Stop: _operations.StopAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Restart: _operations.RestartAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Update: _operations.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
            case ConfirmationKind.Backup: _operations.CreateBackupAsync(instance, Arg.Any<CancellationToken>()).Returns(task); break;
        }
    }

    private async Task ReceivedOp(ConfirmationKind kind, string instance)
    {
        switch (kind)
        {
            case ConfirmationKind.Start: await _operations.Received(1).StartAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Stop: await _operations.Received(1).StopAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Restart: await _operations.Received(1).RestartAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Update: await _operations.Received(1).UpdateAsync(instance, Arg.Any<CancellationToken>()); break;
            case ConfirmationKind.Backup: await _operations.Received(1).CreateBackupAsync(instance, Arg.Any<CancellationToken>()); break;
        }
    }

    [Theory]
    [InlineData("boot")]   // not a verb we know
    [InlineData("")]       // blank
    public async Task ServerCommand_InvalidVerb_IsRefused_AndStagesNothing(string verb)
    {
        // Defense-in-depth behind the schema enum: a verb the dispatcher doesn't recognise is
        // refused (with the valid verbs listed) before any instance resolution or staging.
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Summary(ServerCommandCall(verb, "minecraft"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("not a valid server action");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ServerCommand_UnresolvedTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Summary(ServerCommandCall("start", "doesnotexist"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive().StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallServer_StagesConfirmation_AndDoesNotExecute()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Summary(Call(ShippedText.Name(LlmTools.UninstallServer), "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
    }

    [Fact]
    public async Task UninstallServer_AmbiguousTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Summary(Call(ShippedText.Name(LlmTools.UninstallServer), "terraria"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task InstallServer_ResolvesBlueprint_AndStagesConfirmation()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(InstallCall("valheim", "my-valheim"));

            result.Should().Contain("Staged");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new PendingConfirmation(ConfirmationKind.Install, "valheim", "my-valheim"));
        }
    }

    // --- the catalog is written in game names, and a game name is what comes back ---

    [Fact]
    public async Task BlueprintInfo_ListsGamesByTheNamePeopleUse_NotTheBlueprintIdentifier()
    {
        var result = await Summary(new LlmToolCall(ShippedText.Name(LlmTools.BlueprintInfo), new Dictionary<string, string?>()));

        result.Should().Contain("Project Zomboid");
        result.Should().NotContain("projectzomboid");
    }

    [Fact]
    public async Task BlueprintInfo_ByDisplayName_ReadsTheBlueprintTheEngineKnows()
    {
        _inventory.GetBlueprintDetailAsync("projectzomboid", Arg.Any<CancellationToken>())
            .Returns(new BlueprintDetail(
                "projectzomboid", "Project Zomboid", null, [], "native", false, null, null, null, null, []));

        var result = await Summary(new LlmToolCall(ShippedText.Name(LlmTools.BlueprintInfo),
            new Dictionary<string, string?> { ["blueprint_name"] = "Project Zomboid" }));

        result.Should().Contain("Project Zomboid");
        await _inventory.Received(1).GetBlueprintDetailAsync("projectzomboid", Arg.Any<CancellationToken>());
    }

    // A display name is for saying; what gets staged is always the identifier the engine installs, so
    // the confirmation runs against `projectzomboid` however the model spelled the game.
    [Theory]
    [InlineData("Project Zomboid")]
    [InlineData("project zomboid")]
    [InlineData("Project-Zomboid")]
    [InlineData("projectzomboid")]
    public async Task InstallServer_ByAnySpellingOfTheGame_StagesTheBlueprintIdentifier(string spoken)
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(InstallCall(spoken, "pz"));

            result.Should().Contain("Staged").And.Contain("Project Zomboid");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Target.Should().Be("projectzomboid");
        }
    }

    [Fact]
    public async Task InstallServer_WithALibrary_StagesTheNameForTheConfirmationToShow()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(new LlmToolCall(ShippedText.Name(LlmTools.InstallServer),
                new Dictionary<string, string?>
                {
                    ["blueprint_name"] = "valheim",
                    ["library"] = "ssd",
                }));

            result.Should().Contain("Staged").And.Contain("ssd");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Library.Should().Be("ssd");
        }
    }

    // Placement is the engine's to resolve when nobody named a library, and staging one here would
    // be this process deciding it — the confirmation would then show a disk nobody chose.
    [Fact]
    public async Task InstallServer_WithNoLibrary_StagesNone()
    {
        using (_confirmations.BeginTurn())
        {
            await Summary(InstallCall("valheim", "my-valheim"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Library.Should().BeNull();
        }
    }

    [Fact]
    public async Task InstallServer_UnknownGame_ListsTheGamesByName()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(InstallCall("Half-Life"));

            result.Should().Contain("Project Zomboid");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task InstallServer_NameCollision_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(InstallCall("valheim", "minecraft"));

            result.Should().Contain("already exists");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SetConfig_StagesConfirmation_WithKeyAndValue_AndDoesNotWrite()
    {
        using (_confirmations.BeginTurn())
        {
            // A value with spaces and an '=' — the prime executable_arguments case.
            var result = await Summary(
                SetConfigCall("minecraft", "executable_arguments", "--foo=bar baz"));

            result.Should().Contain("Staged").And.Contain("confirm");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(
                    ConfirmationKind.SetConfig, "minecraft",
                    InstanceName: null, ConfigKey: "executable_arguments", ConfigValue: "--foo=bar baz"));
        }

        // Propose-only: nothing is written inline (kgsk runs only after a human confirms).
        await _operations.DidNotReceive().SetInstanceConfigValueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetConfig_BlankKey_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetConfigCall("minecraft", "   ", "x"));

            result.Should().Contain("no config_key");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SetConfig_UnknownInstance_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetConfigCall("doesnotexist", "auto_update", "true"));

            result.Should().Contain("no instance named");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("note")]
    [InlineData("note_updated_by")]
    [InlineData("note_updated_at")]
    [InlineData("NOTE")]
    public async Task SetConfig_ServerNoteKey_DoesNotStage(string key)
    {
        // The server note is player-facing text with its own panel surface, which owns the encoding
        // and records who wrote it. kgsm would accept these keys as ordinary runtime values, so the
        // gate lives here: a chat turn must not be able to rewrite a note raw and unattributed.
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetConfigCall("minecraft", key, "anything"));

            result.Should().Contain("not editable from chat");
            _confirmations.Staged.Should().BeEmpty();
        }

        await _operations.DidNotReceive().SetInstanceConfigValueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- write_file staging ------------------------------------------------------------------

    /// <summary>Stands in for the file on disk: the edit is resolved against it by the port, exactly
    /// as the adapter does, so the dispatcher is tested on what it stages, not on how it edits.</summary>
    private void FileHolds(string instance, string path, string content) =>
        _operations.PrepareInstanceFileEditAsync(
                instance, path, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var edit = TheKrystalShip.Kgsm.Assistant.Files.FileEdit.Apply(
                    content, ci.ArgAt<string>(2), ci.ArgAt<string>(3));
                return edit.IsApplied
                    ? Result.Success(edit.Content!)
                    : Result.Failure<string>($"the text to replace is not in '{path}'.");
            });

    [Fact]
    public async Task WriteFile_StagesTheFileWithTheEditApplied_AndDoesNotWrite()
    {
        FileHolds("minecraft", "server.properties", "motd=old\nmax-players=20\npvp=true\n");

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(
                WriteFileCall("minecraft", "server.properties", "motd=old", "motd=hello world"));

            result.Should().Contain("Staged").And.Contain("confirm");

            // The staged payload is the WHOLE file with one line changed — the settings the model
            // never sent are still there, which is the point of carrying an edit.
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(
                    ConfirmationKind.WriteFile, "minecraft",
                    InstanceName: null, ConfigKey: "server.properties",
                    ConfigValue: "motd=hello world\nmax-players=20\npvp=true\n"));
        }

        // Propose-only: nothing is written inline (the write runs only after a human confirms).
        await _operations.DidNotReceive().WriteInstanceFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_PassesTheEditAndTheSeedFileThrough()
    {
        _operations.PrepareInstanceFileEditAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Difficulty=Hard\n"));

        using (_confirmations.BeginTurn())
        {
            await Summary(WriteFileCall(
                "minecraft", "live.ini", "Difficulty=None", "Difficulty=Hard", "defaults.ini"));
        }

        await _operations.Received(1).PrepareInstanceFileEditAsync(
            "minecraft", "live.ini", "Difficulty=None", "Difficulty=Hard", "defaults.ini",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_EditThatDoesNotApply_IsRefused_AndStagesNothing()
    {
        FileHolds("minecraft", "server.properties", "motd=old\n");

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(
                WriteFileCall("minecraft", "server.properties", "mtod=old", "motd=new"));

            result.Should().StartWith("Error:").And.Contain("Nothing was staged");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WriteFile_BlankPath_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "   ", "a", "b"));

            result.Should().Contain("no path");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WriteFile_MissingOldString_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "server.properties", null, "motd=new"));

            result.Should().Contain("no old_string");
            _confirmations.Staged.Should().BeEmpty();
        }

        // Refused before the file is even read — an editless call is not a proposal.
        await _operations.DidNotReceive().PrepareInstanceFileEditAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteFile_MissingNewString_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "server.properties", "motd=old", null));

            result.Should().Contain("no new_string");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WriteFile_AnEmptyAnchorIsAllowedWhenSeedingFromAReferenceFile()
    {
        _operations.PrepareInstanceFileEditAsync(
                "minecraft", "live.ini", "", "", "defaults.ini", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Difficulty=None\n"));

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "live.ini", "", "", "defaults.ini"));

            result.Should().Contain("Staged").And.Contain("defaults.ini");
            _confirmations.Staged.Should().ContainSingle()
                .Which.ConfigValue.Should().Be("Difficulty=None\n");
        }
    }

    [Fact]
    public async Task WriteFile_UnknownInstance_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("doesnotexist", "server.properties", "a", "b"));

            result.Should().Contain("no instance named");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WriteFile_AnEditThatEmptiesTheFile_IsRefused_BeforeStaging()
    {
        FileHolds("minecraft", "server.properties", "motd=old\n");

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "server.properties", "motd=old\n", ""));

            result.Should().Contain("empty");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WriteFile_OversizedResult_IsRefused_BeforeStaging()
    {
        var huge = new string('a', 10 * 1024 * 1024 + 1); // one byte over the 10 MB cap
        _operations.PrepareInstanceFileEditAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(huge));

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(WriteFileCall("minecraft", "big.txt", "a", "b"));

            result.Should().Contain("MB limit");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    private static LlmToolCall SearchFilesCall(string instance, string argument, string value) =>
        new(ShippedText.Name(LlmTools.SearchFiles), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            [argument] = value,
        });

    [Fact]
    public async Task SearchFiles_GlobArgument_IsNamedAsTheMistake_AndNeverSearched()
    {
        // "*Player*" is a valid FILENAME glob and an invalid expression. Relaying the regex parser's
        // complaint reads as "the search failed", which earns a retry with another glob — the loop
        // that cost this case its whole turn.
        var result = await Summary(SearchFilesCall("minecraft", "text", "*Player*"));

        result.Should().Contain("not a filename pattern");
        result.Should().Contain("find_instance_file");
        await _operations.DidNotReceiveWithAnyArgs()
            .SearchInstanceFilesAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SearchFiles_AcceptsPattern_AsAnAliasForText()
    {
        // find_files' argument is named "pattern"; a model that just used it reaches for the same word.
        _operations.SearchInstanceFilesAsync("minecraft", "MaxPlayers", null, true, Arg.Any<CancellationToken>())
            .Returns(Result<InstanceContentMatches>.Success(
                new InstanceContentMatches(new[] { new InstanceContentMatch("server.properties", 12, "MaxPlayers=20") }, false, false)));

        var result = await Summary(SearchFilesCall("minecraft", "pattern", "MaxPlayers"));

        result.Should().Contain("server.properties");
    }

    // --- read_console: which run, and saying so -------------------------------------------------

    /// <summary>
    /// A console that has runs. <paramref name="Runs"/> is what the run list reports;
    /// <paramref name="LinesByRun"/> is what each run's output holds.
    /// </summary>
    private sealed record StubConsole(
        IReadOnlyList<ConsoleRunInfo> Runs,
        IReadOnlyDictionary<int, IReadOnlyList<string>> LinesByRun) : IServerFacts
    {
        public Task<ConsoleRuns> GetConsoleRunsAsync(string instance, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleRuns(FactsState.Available, Runs));

        public Task<ConsoleTail> GetConsoleRunTailAsync(
            string instance, int lines, int run, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleTail(
                FactsState.Available,
                LinesByRun.TryGetValue(run, out var l) ? l : []));

        public Task<ConsoleTail> GetConsoleTailAsync(string instance, int lines, CancellationToken ct = default) =>
            GetConsoleRunTailAsync(instance, lines, 0, ct);

        public Task<BackupListing> GetBackupsAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new BackupListing(FactsState.Unavailable, []));
        public Task<VersionFacts> GetVersionAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new VersionFacts(FactsState.Unavailable, null, null, null));
        public Task<PresenceReading> GetPresenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new PresenceReading(FactsState.Unavailable, []));
        public Task<AutostartReading> GetAutostartAsync(CancellationToken ct = default) =>
            Task.FromResult(new AutostartReading(FactsState.Unavailable, []));
        public Task<InstanceStatusFacts> GetStatusAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new InstanceStatusFacts(
                FactsState.Unavailable, false, null, null, null, null, null, null, [], null, null, null, 0));
        public Task<InstanceConfigFacts> GetConfigAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new InstanceConfigFacts(FactsState.Unavailable, []));
        public Task<NoteFacts> GetNoteAsync(string i, CancellationToken ct = default) =>
            Task.FromResult(new NoteFacts(FactsState.Unavailable, null, null, null));
    }

    private static LlmToolCall ReadConsoleCall(string instance, string? run = null) =>
        new(ShippedText.Name(LlmTools.ReadConsole), new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["run"] = run,
        });

    private static StubConsole CrashedThenRestarted() => new(
        Runs: new[]
        {
            new ConsoleRunInfo(0, Current: true, EndedAt: null),
            new ConsoleRunInfo(1, Current: false, DateTimeOffset.UtcNow.AddMinutes(-6),
                ConsoleRunInfo.CrashedOutcome, ExitCode: 139),
        },
        LinesByRun: new Dictionary<int, IReadOnlyList<string>>
        {
            [0] = new[] { "Server ready." },
            [1] = new[] { "Unhandled exception. System.NullReferenceException" },
        });

    [Fact]
    public async Task ReadConsole_DefaultsToTheCurrentRun_AndSaysTheCrashIsInTheOneBefore()
    {
        // The original failure: after a crash-restart the default read is a clean boot, and handed over
        // bare it reads as "no errors". The lines are still the lines — what changes is that they now
        // arrive labelled, with the crashed run named and reachable.
        var result = await Create(CrashedThenRestarted()).ExecuteAsync(ReadConsoleCall("minecraft"));

        result.Summary.Should().Contain("Server ready.");
        result.Summary.Should().Contain("run 0, the run in progress");
        result.Summary.Should().Contain("ended in a crash").And.Contain("exit 139");
        result.Summary.Should().Contain("run=1");
    }

    [Fact]
    public async Task ReadConsole_ReadsTheRunItWasAskedFor()
    {
        var result = await Create(CrashedThenRestarted()).ExecuteAsync(ReadConsoleCall("minecraft", run: "1"));

        result.Summary.Should().Contain("Unhandled exception");
        result.Summary.Should().NotContain("Server ready.");
    }

    [Fact]
    public async Task ReadConsole_RefusesARunThatDoesNotExist_AndSaysHowManyThereAre()
    {
        // Answering a missing run with run 0's output would hand back a clean boot under the label of
        // the run that was asked for — the same confusion, now with the tool's authority behind it.
        var result = await Create(CrashedThenRestarted()).ExecuteAsync(ReadConsoleCall("minecraft", run: "7"));

        result.Summary.Should().Contain("no run 7").And.Contain("2 run(s)");
        result.Summary.Should().NotContain("Server ready.");
    }

    [Fact]
    public async Task ReadConsole_WithNoRunList_StillReturnsTheOutput()
    {
        // The run list is what places the output, not what permits it. A supervisor that serves the
        // lines but not the list still answers with the lines, unlabelled.
        var facts = new StubConsole(
            Runs: [],
            LinesByRun: new Dictionary<int, IReadOnlyList<string>> { [0] = new[] { "Server ready." } });

        var result = await Create(facts).ExecuteAsync(ReadConsoleCall("minecraft"));

        result.Summary.Should().Contain("Server ready.").And.Contain("Recent console output for minecraft");
    }

    [Fact]
    public async Task Search_WhenTheUserAskedToLookOnline_ReachesTheWebEvenThoughTheModelDidNotSaySo()
    {
        // ⚠ The measured failure, isolated: asked in plain English to check online, gemma4:12b called
        // search with the query alone and no scope, so the default ladder ran and local docs answered.
        // The turn's intent has to win, or an explicit instruction depends on the model's discretion.
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("Web results …", SearchState.Web));

        using (SearchIntent.BeginTurn(SearchScope.Web))
            await Create().ExecuteAsync(SearchCall("newest terraria version"));

        await _search.Received(1).SearchAsync(
            "newest terraria version", SearchScope.Web, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_TheUsersOwnWordsOutrankTheModelsScope()
    {
        // The scope on the call is the model READING the request; the turn's intent IS the request.
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("Web results …", SearchState.Web));

        var call = new LlmToolCall(ShippedText.Name(LlmTools.Search),
            new Dictionary<string, string?> { ["query"] = "newest terraria version", ["scope"] = "local" });

        using (SearchIntent.BeginTurn(SearchScope.Web))
            await Create().ExecuteAsync(call);

        await _search.Received(1).SearchAsync(
            "newest terraria version", SearchScope.Web, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_OnAnOrdinaryTurn_TheModelsChoiceStands()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchScope>(), Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("From the docs …", SearchState.LocalStrong));

        await Create().ExecuteAsync(SearchCall("what is kgsm"));

        await _search.Received(1).SearchAsync(
            "what is kgsm", SearchScope.Auto, Arg.Any<CancellationToken>());
    }

    // --- set_game_setting staging (address by key; the path is resolved, not transcribed) -------

    /// <summary>The instance's files, as the finder sees them — the lookup that spares the model from
    /// copying a path back.</summary>
    private void InstanceContains(string instance, params string[] paths) =>
        _operations.FindInstanceFilesAsync(instance, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result.Success(new InstanceFileMatches(
                paths.Where(p => p.EndsWith("/" + ci.ArgAt<string>(1), StringComparison.Ordinal)
                              || p == ci.ArgAt<string>(1)).ToList(),
                Truncated: false, Incomplete: false)));

    private void SettingHolds(string instance, string path, string previous, string content) =>
        _operations.PrepareInstanceSettingEditAsync(
                instance, path, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result.Success(new SettingEditSummary(content, previous, ci.ArgAt<string>(3))));

    [Fact]
    public async Task SetGameSetting_StagesTheEditedFile_AndDoesNotWrite()
    {
        InstanceContains("minecraft", "server.properties");
        SettingHolds("minecraft", "server.properties", "easy", "difficulty=hard\n");

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetGameSettingCall("minecraft", "server.properties", "difficulty", "hard"));

            result.Should().Contain("Staged").And.Contain("confirm");
            // The value it replaced is named: the model never read the file, so this is the only way
            // it can tell a person what actually changed.
            result.Should().Contain("easy").And.Contain("hard");

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(
                    ConfirmationKind.WriteFile, "minecraft",
                    InstanceName: null, ConfigKey: "server.properties",
                    ConfigValue: "difficulty=hard\n"));
        }

        await _operations.DidNotReceive().WriteInstanceFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetGameSetting_CorrectsAMistranscribedDirectory()
    {
        // The measured failure: the model is handed the real path and hands back a corrupted one. The
        // file name survived, so the edit still reaches the file the game actually reads.
        InstanceContains("minecraft", "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        SettingHolds("minecraft", "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", "1.000000", "edited");

        using (_confirmations.BeginTurn())
        {
            await Summary(SetGameSettingCall(
                "minecraft", "install/Pal/Saved/Config/Linux_Server/PalWorldSettings.ini",
                "DayTimeSpeedRate", "0.500000"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.ConfigKey.Should().Be("install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        }
    }

    [Fact]
    public async Task SetGameSetting_AcceptsABareFileName()
    {
        InstanceContains("minecraft", "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        SettingHolds("minecraft", "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", "1.000000", "edited");

        using (_confirmations.BeginTurn())
        {
            await Summary(SetGameSettingCall("minecraft", "PalWorldSettings.ini", "DayTimeSpeedRate", "0.500000"));

            await _operations.Received(1).FindInstanceFilesAsync(
                "minecraft", "PalWorldSettings.ini", Arg.Any<string?>(), Arg.Any<CancellationToken>());
            _confirmations.Staged.Should().ContainSingle()
                .Which.ConfigKey.Should().Be("install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        }
    }

    [Fact]
    public async Task SetGameSetting_NameMatchesSeveralFiles_ListsThemAndStagesNothing()
    {
        InstanceContains("minecraft", "a/settings.ini", "b/settings.ini");

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetGameSettingCall("minecraft", "settings.ini", "Difficulty", "Hard"));

            result.Should().StartWith("Error:").And.Contain("a/settings.ini").And.Contain("b/settings.ini");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SetGameSetting_NoSuchFile_SaysSoAndStagesNothing()
    {
        InstanceContains("minecraft", "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");

        using (_confirmations.BeginTurn())
        {
            // The name itself was corrupted, which no lookup can recover — it has to fail loudly.
            var result = await Summary(SetGameSettingCall("minecraft", "PaulWorldSettings.ini", "Difficulty", "Hard"));

            result.Should().StartWith("Error:").And.Contain("find_instance_file");
            _confirmations.Staged.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SetGameSetting_AMissingSettingStagesNothing()
    {
        InstanceContains("minecraft", "server.properties");
        _operations.PrepareInstanceSettingEditAsync(
                "minecraft", "server.properties", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SettingEditSummary>("'pvp' is not a setting in 'server.properties'."));

        using (_confirmations.BeginTurn())
        {
            var result = await Summary(SetGameSettingCall("minecraft", "server.properties", "pvp", "false"));

            result.Should().StartWith("Error:").And.Contain("Nothing was staged");
            _confirmations.Staged.Should().BeEmpty();
        }
    }
}
