using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Kgsm.Assistant.Status;
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
    private readonly IServerMetrics _metrics = Substitute.For<IServerMetrics>();
    private readonly IEventHistory _events = Substitute.For<IEventHistory>();
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

        var blueprints = new[] { "valheim", "terraria" };
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)blueprints);
    }

    private ToolDispatcher Create() =>
        new(_operations, _inventory, _confirmations, _search, _metrics, _events, NullLogger<ToolDispatcher>.Instance);

    // Phase 2: ExecuteAsync now returns ToolOutput (model-facing summary + optional surface card). The
    // routing/resolution/staging tests below assert on the model-facing summary, so unwrap it once here.
    private async Task<string> Summary(LlmToolCall call) => (await Create().ExecuteAsync(call)).Summary;

    private static LlmToolCall Call(Tool name, string instance) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = instance });

    private static LlmToolCall ServerCommandCall(string verb, string instance) =>
        new(LlmTools.ServerCommand, new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["verb"] = verb,
        });

    private static LlmToolCall InstallCall(string blueprint, string? name = null) =>
        new(LlmTools.InstallServer, new Dictionary<string, string?>
        {
            ["blueprint_name"] = blueprint,
            ["instance_name"] = name,
        });

    private static LlmToolCall SetConfigCall(string instance, string? key, string? value) =>
        new(LlmTools.SetConfigValue, new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["config_key"] = key,
            ["config_value"] = value,
        });

    private static LlmToolCall SearchCall(string? query) =>
        new(LlmTools.Search, new Dictionary<string, string?> { ["query"] = query });

    [Fact]
    public async Task ExactName_Resolves_AndExecutes()
    {
        _operations.GetStatusAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success("running, pid 123"));

        var result = await Summary(Call(LlmTools.GetStatus, "minecraft"));

        result.Should().Contain("Status for minecraft");
        await _operations.Received(1).GetStatusAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleFuzzyMatch_Resolves()
    {
        _operations.GetStatusAsync("terraria-pvp", Arg.Any<CancellationToken>())
            .Returns(Result.Success("stopped"));

        // "pvp" is a substring of exactly one instance.
        await Summary(Call(LlmTools.GetStatus, "pvp"));

        await _operations.Received(1).GetStatusAsync("terraria-pvp", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousName_AsksUser_AndDoesNotExecute()
    {
        // "terraria" matches two instances by game type / substring.
        var result = await Summary(Call(LlmTools.GetStatus, "terraria"));

        result.Should().Contain("Ambiguous")
            .And.Contain("terraria-pvp")
            .And.Contain("terraria-creative");
        await _operations.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownName_ReturnsMiss_WithKnownList()
    {
        var result = await Summary(Call(LlmTools.GetStatus, "doesnotexist"));

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
            new LlmToolCall(LlmTools.GetStatus, new Dictionary<string, string?>()));

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
            new LlmToolCall(LlmTools.GetStatus, new Dictionary<string, string?>()));

        output.Summary.Should().Contain("minecraft: running").And.Contain("broken: status unavailable");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(LlmTools.GetStatus.Name);
        card.Subject.Should().Be(new ResultRef(ResourceKind.Host, "primary"));
        var data = card.Data.Should().BeOfType<FleetStatusData>().Subject;
        data.Running.Should().Be(1);
        data.Unavailable.Should().Be(1);
        // The §3.7 guard carried into the card: the unreadable instance is Unknown, never Stopped.
        data.Servers.Single(s => s.Instance == "broken").State.Should().Be(ServerRunState.Unknown);
        data.Stopped.Should().Be(0);
    }

    [Fact]
    public async Task GetStatus_SingleServer_StaysCardless()
    {
        // The single-server path returns kgsm's opaque status string — no structured source — so it
        // is summary-only (Data null); carding it would fabricate structure we don't have.
        _operations.GetStatusAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success("running, pid 123"));

        var output = await Create().ExecuteAsync(Call(LlmTools.GetStatus, "minecraft"));

        output.Summary.Should().Contain("Status for minecraft");
        output.Data.Should().BeNull();
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
            new LlmToolCall(LlmTools.GetStatus, new Dictionary<string, string?>()));

        // The §3.7 guard: a could-not-read instance must not masquerade as stopped.
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
                UpdatesAvailable: false,
                CurrentVersion: "1.0.0",
                LatestVersion: null,
                HostDisk: new HostDisk(26, "916G", "649G"),
                HostDiskUnavailableReason: null)));

        var result = await Summary(Call(LlmTools.RunHealthCheck, "minecraft"));

        // The dispatcher returns the aggregator's deterministic summary (the model's grounding text).
        result.Should().Contain("minecraft").And.Contain("healthy");
        await _operations.Received(1).GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHealthCheck_UnresolvedInstance_DoesNotFetch()
    {
        var result = await Summary(Call(LlmTools.RunHealthCheck, "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHealthCheck_PortFailure_ReturnsError()
    {
        _operations.GetHealthSnapshotAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<InstanceHealthSnapshot>("kgsm unreachable"));

        var result = await Summary(Call(LlmTools.RunHealthCheck, "minecraft"));

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
                UpdatesAvailable: true,
                CurrentVersion: "1.0.0",
                LatestVersion: "1.1.0",
                HostDisk: new HostDisk(26, "916G", "649G"),
                HostDiskUnavailableReason: null)));

        var output = await Create().ExecuteAsync(Call(LlmTools.RunHealthCheck, "minecraft"));

        output.Summary.Should().Contain("minecraft");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(LlmTools.RunHealthCheck.Name);
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

        var result = await Summary(Call(LlmTools.ReadFile, "minecraft"));

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

        var call = new LlmToolCall(LlmTools.ReadFile, new Dictionary<string, string?>
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
        var result = await Summary(Call(LlmTools.ReadFile, "doesnotexist"));

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

        var result = await Summary(Call(LlmTools.ListFiles, "minecraft"));

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

        var call = new LlmToolCall(LlmTools.ListFiles, new Dictionary<string, string?>
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
        var result = await Summary(Call(LlmTools.ListFiles, "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .ListInstanceDirectoryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Summary(
            new LlmToolCall(new Tool("delete_everything"), new Dictionary<string, string?>()));

        result.Should().Contain("not a known tool");
    }

    // --- search (unified lookup via the ISearch aggregator) ---
    // The dispatcher only guards the blank query and relays the aggregator's grounding text verbatim;
    // the local-first / web-fallback composition is exercised in SearchAggregatorTests.

    private static ToolResult<SearchData> SearchEnvelope(string summary, SearchState state, params SearchPassage[] passages) =>
        new(LlmTools.Search, Confidence.Likely, new ResultRef(ResourceKind.Search, "q"), summary, new SearchData("q", state, passages));

    [Fact]
    public async Task Search_RelaysTheAggregatorGroundingVerbatim()
    {
        _search.SearchAsync("terraria latest version", Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("Web results for \"terraria latest version\" … source: https://terraria.org/news",
                SearchState.Web, new SearchPassage(SearchProvenance.Web, "https://terraria.org/news", "News", "…", 0.9)));

        var result = await Summary(SearchCall("terraria latest version"));

        result.Should().Be("Web results for \"terraria latest version\" … source: https://terraria.org/news");
        await _search.Received(1).SearchAsync("terraria latest version", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_WithPassages_SurfacesTheSearchCard()
    {
        _search.SearchAsync("what is kgsm", Arg.Any<CancellationToken>())
            .Returns(SearchEnvelope("From the operator's indexed docs …", SearchState.LocalStrong,
                new SearchPassage(SearchProvenance.Local, "docs/x.md", "X > Y", "KGSM …", 0.8)));

        var output = await Create().ExecuteAsync(SearchCall("what is kgsm"));

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(LlmTools.Search.Name);   // the card carries the tool NAME ("search")
        card.Data.Should().BeOfType<SearchData>().Which.State.Should().Be(SearchState.LocalStrong);
    }

    [Fact]
    public async Task Search_WithNoPassages_StaysSummaryOnly()
    {
        _search.SearchAsync("nonexistent", Arg.Any<CancellationToken>())
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
        await _search.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- get_performance (live per-server metrics via the IServerMetrics port) ---
    // The dispatcher resolves the instance, reads the neutral snapshot, runs the pure aggregator, and
    // attaches a card ONLY for a Live read; the Live/NotRunning/Unavailable wording lives in PerformanceReportTests.

    private static LlmToolCall PerformanceCall(string instance) => Call(LlmTools.GetPerformance, instance);

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
        card.Tool.Should().Be(LlmTools.GetPerformance.Name);   // the card carries the tool NAME ("get_performance")
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

    private static LlmToolCall PerformanceTrendCall(string instance, string range) =>
        new(LlmTools.GetPerformance, new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["range"] = range,
        });

    [Fact]
    public async Task GetPerformance_WithRange_ReadsHistory_AndSurfacesTheChartCard()
    {
        var t0 = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        _metrics.GetHistoryAsync("minecraft", "1h", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsHistory(
                PerformanceState.Live, "1h", "raw",
                new Dictionary<string, IReadOnlyList<MetricPoint>>
                {
                    ["cpuPctCore"] = [new MetricPoint(t0, 10), new MetricPoint(t0.AddMinutes(1), 30)],
                }));

        var output = await Create().ExecuteAsync(PerformanceTrendCall("minecraft", "1h"));

        await _metrics.Received(1).GetHistoryAsync("minecraft", "1h", Arg.Any<CancellationToken>());
        await _metrics.DidNotReceive().GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        output.Summary.Should().Contain("last 1h");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        var data = card.Data.Should().BeOfType<PerformanceData>().Subject;
        data.Range.Should().Be("1h");
        data.Series.Should().ContainKey("cpuPctCore");
    }

    [Fact]
    public async Task GetPerformance_WithRange_EmptyHistory_StaysSummaryOnly()
    {
        _metrics.GetHistoryAsync("minecraft", "24h", Arg.Any<CancellationToken>())
            .Returns(new ServerMetricsHistory(
                PerformanceState.Live, "24h", "raw", new Dictionary<string, IReadOnlyList<MetricPoint>>()));

        var output = await Create().ExecuteAsync(PerformanceTrendCall("minecraft", "24h"));

        output.Summary.Should().Contain("No resource history");
        output.Data.Should().BeNull();   // empty series → no chart card
    }

    // --- get_audit_log / get_change_timeline (engine event history via the IEventHistory port) ---
    // The dispatcher resolves an OPTIONAL instance (blank → fleet-wide), maps window/range to a
    // `since` bound, reads the raw rows, and runs the pure AuditReport composer — always attaching a
    // card (an empty/unavailable result is still a real, honestly-worded answer worth showing).

    private static LlmToolCall AuditLogCall(string? instance = null, string? window = null) =>
        new(LlmTools.GetAuditLog, new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["window"] = window,
        });

    private static LlmToolCall ChangeTimelineCall(string? instance = null, string? range = null) =>
        new(LlmTools.GetChangeTimeline, new Dictionary<string, string?>
        {
            ["instance_name"] = instance,
            ["range"] = range,
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
        card.Tool.Should().Be(LlmTools.GetAuditLog.Name);
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
            .Returns(new EventHistoryReading(AuditReadState.MonitorUnavailable, Array.Empty<AuditEventRow>()));

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
    public async Task GetChangeTimeline_NoInstance_ReadsFleetWide_UsesChangeFraming()
    {
        var changeRow = SampleStart with { Type = "instance_installed" };
        _events.GetEventsAsync(null, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, new[] { changeRow }));

        var output = await Create().ExecuteAsync(ChangeTimelineCall());

        await _events.Received(1).GetEventsAsync(null, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        output.Summary.Should().Contain("1 change for all servers");
        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        card.Tool.Should().Be(LlmTools.GetChangeTimeline.Name);
    }

    [Fact]
    public async Task GetChangeTimeline_FiltersOutRoutineEvents_ViaTheSharedComposer()
    {
        var routine = SampleStart;   // instance_started — not a "change"
        _events.GetEventsAsync("minecraft", Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EventHistoryReading(AuditReadState.Available, new[] { routine }));

        var output = await Create().ExecuteAsync(ChangeTimelineCall("minecraft"));

        var card = output.Data.Should().BeOfType<ToolResultCard>().Subject;
        var data = card.Data.Should().BeOfType<AuditData>().Subject;
        data.Events.Should().BeEmpty();
        output.Summary.Should().Contain("No changes recorded");
    }

    [Fact]
    public async Task GetChangeTimeline_UnresolvedInstance_DoesNotRead()
    {
        var output = await Create().ExecuteAsync(ChangeTimelineCall("doesnotexist"));

        output.Summary.Should().Contain("no instance named");
        await _events.DidNotReceive().GetEventsAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
        // §3.5 + §4.1: the merged server_command routes its `verb` to the matching kind and
        // STAGES it; the single-instance op runs later (from ConfirmAsync), never inline.
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
            result = await Summary(Call(LlmTools.UninstallServer, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
        await _operations.DidNotReceive().UninstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

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
            result = await Summary(Call(LlmTools.UninstallServer, "minecraft"));

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
            result = await Summary(Call(LlmTools.UninstallServer, "terraria"));
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
}
