using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
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
    private readonly IWebSearch _webSearch = Substitute.For<IWebSearch>();
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
        new(_operations, _inventory, _confirmations, _webSearch, NullLogger<ToolDispatcher>.Instance);

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
        new(LlmTools.WebSearch, new Dictionary<string, string?> { ["query"] = query });

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
    public async Task ViewConfigFile_ReadsResolvedInstanceConfig_AndRedactsSecrets()
    {
        _operations.ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>())
            .Returns(Result.Success("port = 25565\nrcon_password = hunter2\nlevel = world"));

        var result = await Summary(Call(LlmTools.ViewConfigFile, "minecraft"));

        // The filename is derived from the resolved instance name (no model-supplied path).
        await _operations.Received(1)
            .ReadInstanceFileAsync("minecraft", "minecraft.config.ini", Arg.Any<CancellationToken>());

        result.Should().Contain("port = 25565").And.Contain("level = world");
        result.Should().Contain("rcon_password").And.Contain("***redacted***");
        result.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task ViewConfigFile_UnknownInstance_DoesNotRead()
    {
        var result = await Summary(Call(LlmTools.ViewConfigFile, "doesnotexist"));

        result.Should().Contain("no instance named");
        await _operations.DidNotReceive()
            .ReadInstanceFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Summary(
            new LlmToolCall(new Tool("delete_everything"), new Dictionary<string, string?>()));

        result.Should().Contain("not a known tool");
    }

    // --- web_search (external lookup via the IWebSearch port) ---

    [Fact]
    public async Task WebSearch_ReturnsHits_AsGroundingStringWithSourceUrls()
    {
        _webSearch.SearchAsync("terraria latest version", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>(new[]
            {
                new WebSearchHit("Terraria 1.4.5", "https://terraria.org/news",
                    "Terraria 1.4.5 is the latest stable release.", 0.98),
            }));

        var result = await Summary(SearchCall("terraria latest version"));

        // Snippet + source URL make it into the grounding text, framed as external/cite-able.
        result.Should().Contain("Terraria 1.4.5")
            .And.Contain("https://terraria.org/news")
            .And.Contain("out of date");
        await _webSearch.Received(1).SearchAsync("terraria latest version", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebSearch_BlankQuery_DoesNotCallProvider()
    {
        var result = await Summary(SearchCall("   "));

        result.Should().Contain("needs a 'query'");
        await _webSearch.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebSearch_ProviderFailure_ReturnsGracefulMessage_AndTellsModelNotToRetry()
    {
        _webSearch.SearchAsync("anything", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<WebSearchHit>>("the daily web-search limit has been reached"));

        var result = await Summary(SearchCall("anything"));

        result.Should().Contain("didn't work")
            .And.Contain("daily web-search limit")
            .And.Contain("do not retry");
    }

    [Fact]
    public async Task WebSearch_NoResults_SaysSo()
    {
        _webSearch.SearchAsync("something obscure", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>(Array.Empty<WebSearchHit>()));

        var result = await Summary(SearchCall("something obscure"));

        result.Should().Contain("No web results");
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
