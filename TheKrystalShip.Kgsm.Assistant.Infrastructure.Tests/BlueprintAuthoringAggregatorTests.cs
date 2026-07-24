using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Covers <see cref="BlueprintAuthoringAggregator"/>'s decision logic against fakes for every port —
/// the existence guard, the feasibility gates, the never-fabricate rule, persist/readback validation,
/// the verify-boots-and-listens bar, the bounded self-repair loop, and — most safety-critical —
/// GUARANTEED teardown of the probe instance on every exit path, including an exception. No real
/// kgsm-lib process is ever spawned; every kgsm-lib port is an NSubstitute fake.
/// </summary>
public sealed class BlueprintAuthoringAggregatorTests
{
    private readonly IBlueprintResearch _research = Substitute.For<IBlueprintResearch>();
    private readonly IBlueprintFiles _files = Substitute.For<IBlueprintFiles>();
    private readonly IBlueprintService _blueprints = Substitute.For<IBlueprintService>();
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IInventoryInvalidation _invalidation = Substitute.For<IInventoryInvalidation>();
    private readonly IBlueprintAttemptStore _attempts = Substitute.For<IBlueprintAttemptStore>();
    private readonly AsyncLocalInvocationContext _invocation = new();
    private readonly ITurnProgress _progress = Substitute.For<ITurnProgress>();

    private BlueprintAuthoringAggregator Create(BlueprintAuthoringOptions? options = null) => new(
        Options.Create(options ?? FastOptions()),
        _research, _files, _blueprints, _instances, _operations, _invalidation, _attempts, _invocation, _progress,
        NullLogger<BlueprintAuthoringAggregator>.Instance);

    /// <summary>Short timeouts so a verify loop that never succeeds still finishes fast in tests.</summary>
    private static BlueprintAuthoringOptions FastOptions(int maxAttempts = 1) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        VerifyTimeoutSeconds = 1,
        VerifyPollIntervalSeconds = 1,
    };

    private static BlueprintResearchFindings FeasibleFindings(string executableFile = "./start.sh") => new(
        BlueprintFeasibility.Feasible, "Terraria",
        [new BlueprintResearchField("executable_file", executableFile, "https://example.com/setup")],
        ["https://example.com/setup"], "found a native Linux server");

    private static InstanceHealthSnapshot RunningAndListening() =>
        new(Running: true, RecentLogLines: [], UpdatesAvailable: null, CurrentVersion: null, LatestVersion: null,
            HostDisk: null, HostDiskUnavailableReason: null, PortsReachable: true, PortsDetail: null);

    private static InstanceHealthSnapshot NeverBoots() =>
        new(Running: false, RecentLogLines: [], UpdatesAvailable: null, CurrentVersion: null, LatestVersion: null,
            HostDisk: null, HostDiskUnavailableReason: null, PortsReachable: null, PortsDetail: null);

    // --- Gate 0: disabled --------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_ReturnsHonestOutcome_AndNeverTouchesKgsmLib()
    {
        var result = await Create(new BlueprintAuthoringOptions { Enabled = false }).AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Disabled);
        await _research.DidNotReceiveWithAnyArgs().ResearchAsync(default!);
        _blueprints.DidNotReceiveWithAnyArgs().GetInfo(default!);
        _files.DidNotReceiveWithAnyArgs().Create(default!);
        _instances.DidNotReceiveWithAnyArgs().Install(default!);
    }

    // --- Step 1: existence guard --------------------------------------------------------------------

    [Fact]
    public async Task AlreadyExists_ShortCircuits_NeverResearches()
    {
        _blueprints.GetInfo("terraria").Returns(new Blueprint { Name = "terraria" });

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.AlreadyExists);
        result.Data.OfferInstance.Should().BeTrue("the game IS installable — just not newly authored");
        await _research.DidNotReceiveWithAnyArgs().ResearchAsync(default!);
        _files.DidNotReceiveWithAnyArgs().Create(default!);
    }

    // --- Step 3: feasibility gates -------------------------------------------------------------------

    [Theory]
    [InlineData(BlueprintFeasibility.NotSelfHostable)]
    [InlineData(BlueprintFeasibility.NoNativeLinuxServer)]
    [InlineData(BlueprintFeasibility.Inconclusive)]
    public async Task InfeasibleResearch_HonestStop_NeverPersists_AndStashes(BlueprintFeasibility feasibility)
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(feasibility, null, [], [], "nope"));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.NotFeasible);
        _files.DidNotReceiveWithAnyArgs().Create(default!);
        await _attempts.Received(1).RecordAsync(
            Arg.Is<BlueprintAttemptRecord>(r => r.Outcome == BlueprintAuthoringOutcome.NotFeasible), Arg.Any<CancellationToken>());
    }

    // --- Step 4: never fabricate the one required field ----------------------------------------------

    [Fact]
    public async Task FeasibleButNoExecutableSourced_HonestStop_NeverPersists()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.Feasible, "Terraria", [], [], "found nothing concrete"));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _files.DidNotReceiveWithAnyArgs().Create(default!);
    }

    [Fact]
    public async Task UnsourcedFields_StayNullOrDefault_InTheDraft_NeverFabricated()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        NativeBlueprintDraft? captured = null;
        _files.Create(Arg.Do<NativeBlueprintDraft>(d => captured = d), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        await Create().AuthorAsync("Terraria");

        captured.Should().NotBeNull();
        captured!.Native.SteamAppId.Should().Be(0, "an unsourced steam_app_id is the schema's documented 'not a Steam download', never a guess");
        captured.Native.Ports.Should().BeEmpty("an unsourced port is left blank, never a fabricated number");
        captured.Native.ExecutableFile.Should().Be("./start.sh");
    }

    [Fact]
    public async Task SourcedFields_FlowIntoDraft_PortsInUfwFormat_WithLaunchArgsAndReadyRegex()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(new BlueprintResearchFindings(
            BlueprintFeasibility.Feasible, "Terraria",
            [
                new BlueprintResearchField("executable_file", "TerrariaServer.bin.x86_64", "https://s/1"),
                new BlueprintResearchField("executable_arguments", "-autocreate 3 -worldname World", "https://s/2"),
                new BlueprintResearchField("ports", "7777", "https://s/1"),
                new BlueprintResearchField("startup_success_regex", "Server started", "https://s/1"),
            ],
            ["https://s/1"], "found a native Linux server"));
        NativeBlueprintDraft? captured = null;
        _files.Create(Arg.Do<NativeBlueprintDraft>(d => captured = d), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        await Create().AuthorAsync("Terraria");

        captured.Should().NotBeNull();
        captured!.Native.Ports.Should().Be("7777/tcp|7777/udp", "ports render in UFW format (<port>/<proto>), never docker host:container");
        captured.Native.ExecutableArguments.Should().Be("-autocreate 3 -worldname World", "a documented headless launch command is what lets the probe boot non-interactively");
        captured.Native.StartupSuccessRegex.Should().Be("Server started");
    }

    // --- Steps 5-6: persist + validate-by-readback ----------------------------------------------------

    [Fact]
    public async Task PersistFails_StopsBeforeReadback_AndNeverInstalls()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.BlueprintsDirUnavailable));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _blueprints.Received(1).GetInfo("terraria"); // existence guard only — never re-read for a validate we never reached
        _instances.DidNotReceiveWithAnyArgs().Install(default!);
    }

    [Fact]
    public async Task ReadbackFails_RemovesTheFile_AndNeverInstalls()
    {
        // Engine rejects the draft on readback (e.g. a malformed field) — GetInfo returns null both for
        // the existence guard (doesn't exist yet) AND the post-persist readback (still doesn't "exist"
        // as far as the engine is concerned — the write was structurally accepted but not schema-valid).
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _files.Received(1).Remove("terraria");
        _instances.DidNotReceiveWithAnyArgs().Install(default!);
    }

    // --- Steps 7-8: test-install + verify (boots + listens) --------------------------------------------

    [Fact]
    public async Task VerifySucceeds_KeepsTheBlueprint_AndAlwaysTearsDown()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Verified);
        result.Data.BlueprintName.Should().Be("terraria");
        result.Data.ProofLine.Should().NotBeNullOrEmpty();
        result.Data.OfferInstance.Should().BeTrue();
        _files.DidNotReceive().Remove("terraria");
        _instances.Received(1).Uninstall(
            Arg.Is<string>(n => n.StartsWith(BlueprintProbeNaming.Prefix)), Arg.Any<string?>(), Arg.Any<string?>());
        _invalidation.Received().Invalidate();
    }

    [Fact]
    public async Task InstallUsesTheReservedProbeName_NeverAUserFacingName()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        await Create().AuthorAsync("Terraria");

        _instances.Received(1).Install(
            Arg.Is("terraria"), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Is<string?>(n => n != null && BlueprintProbeNaming.IsProbe(n)),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Is<bool?>(true));
    }

    [Fact]
    public async Task VerifyNeverSucceeds_ExhaustsBound_RemovesFile_Stashes_AndAlwaysTearsDown()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(NeverBoots()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        var result = await Create(FastOptions(maxAttempts: 1)).AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _files.Received(1).Remove("terraria");
        _instances.Received(1).Uninstall(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
        await _attempts.Received(1).RecordAsync(
            Arg.Is<BlueprintAttemptRecord>(r => r.Outcome == BlueprintAuthoringOutcome.Failed), Arg.Any<CancellationToken>());
    }

    // --- Step 9: bounded self-repair --------------------------------------------------------------------

    [Fact]
    public async Task NeverVerifying_RetriesUpToMaxAttempts_ThenStops()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(NeverBoots()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        await Create(FastOptions(maxAttempts: 3)).AuthorAsync("Terraria");

        _instances.Received(3).Install(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<bool?>());
        _instances.Received(3).Uninstall(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    // --- Step 10: GUARANTEED teardown, including on an exception -------------------------------------

    [Fact]
    public async Task InstallThrows_TeardownStillRuns()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(x => throw new InvalidOperationException("kgsm process explosion"));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _instances.Received(1).Uninstall(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task VerifyThrows_TeardownStillRuns()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("monitor socket exploded"));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        var result = await Create().AuthorAsync("Terraria");

        result.Data.Outcome.Should().Be(BlueprintAuthoringOutcome.Failed);
        _instances.Received(1).Uninstall(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task PersistFailureNeverAttemptsInstall_SoTeardownIsNotCalledForThatPath()
    {
        // Teardown is only meaningful once an install was actually attempted — asserting it's
        // skipped when there was never a probe to tear down (distinct from the guaranteed-on-exception
        // tests above, where an install WAS attempted).
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.IoError));

        await Create().AuthorAsync("Terraria");

        _instances.DidNotReceiveWithAnyArgs().Uninstall(default!);
    }

    // --- P4: progress narration --------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReportsEveryStepKey_InOrder()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        await Create().AuthorAsync("Terraria");

        Received.InOrder(() =>
        {
            _progress.Report(LlmTools.CreateBlueprint, "research", Arg.Any<string>());
            _progress.Report(LlmTools.CreateBlueprint, "feasibility", Arg.Any<string>());
            _progress.Report(LlmTools.CreateBlueprint, "draft", Arg.Any<string>());
            _progress.Report(LlmTools.CreateBlueprint, "install", Arg.Any<string>());
            _progress.Report(LlmTools.CreateBlueprint, "verify", Arg.Any<string>());
            _progress.Report(LlmTools.CreateBlueprint, "teardown", Arg.Any<string>());
        });
    }

    [Fact]
    public async Task InfeasibleResearch_ReportsResearchAndFeasibility_ButNeverDraftInstallVerifyTeardown()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.NotSelfHostable, null, [], [], "nope"));

        await Create().AuthorAsync("Terraria");

        _progress.Received(1).Report(LlmTools.CreateBlueprint, "research", Arg.Any<string>());
        _progress.Received(1).Report(LlmTools.CreateBlueprint, "feasibility", Arg.Any<string>());
        _progress.DidNotReceive().Report(LlmTools.CreateBlueprint, "draft", Arg.Any<string>());
        _progress.DidNotReceive().Report(LlmTools.CreateBlueprint, "install", Arg.Any<string>());
        _progress.DidNotReceive().Report(LlmTools.CreateBlueprint, "verify", Arg.Any<string>());
        _progress.DidNotReceive().Report(LlmTools.CreateBlueprint, "teardown", Arg.Any<string>());
    }

    [Fact]
    public async Task Disabled_ReportsNoProgress()
    {
        await Create(new BlueprintAuthoringOptions { Enabled = false }).AuthorAsync("Terraria");

        _progress.DidNotReceiveWithAnyArgs().Report(default!, default!, default!);
    }

    [Fact]
    public async Task AlreadyExists_ReportsNoProgress()
    {
        _blueprints.GetInfo("terraria").Returns(new Blueprint { Name = "terraria" });

        await Create().AuthorAsync("Terraria");

        _progress.DidNotReceiveWithAnyArgs().Report(default!, default!, default!);
    }

    // --- subject.id / Reason (the terminal card the web outcome UI reads) --------------------------

    [Fact]
    public async Task Verified_SubjectId_IsTheSlug_AndReasonIsNull()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null, new Blueprint { Name = "terraria" });
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>()).Returns(FeasibleFindings());
        _files.Create(Arg.Any<NativeBlueprintDraft>(), Arg.Any<bool>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));
        _instances.Install(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new KgsmResult(0));
        _operations.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(RunningAndListening()));
        _instances.Uninstall(default!, default, default).ReturnsForAnyArgs(new KgsmResult(0));

        var result = await Create().AuthorAsync("Terraria");

        result.Subject.Id.Should().Be("terraria");
        result.Data.Reason.Should().BeNull();
        result.Data.ProofLine.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InfeasibleResearch_SubjectId_IsTheSlug_AndReasonIsSet()
    {
        _blueprints.GetInfo("terraria").Returns((Blueprint?)null);
        _research.ResearchAsync("Terraria", Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.NotSelfHostable, null, [], [], "nope"));

        var result = await Create().AuthorAsync("Terraria");

        result.Subject.Id.Should().Be("terraria");
        result.Data.Reason.Should().NotBeNullOrEmpty();
    }
}
