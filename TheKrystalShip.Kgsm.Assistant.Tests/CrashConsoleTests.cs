using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.RootCause;

using Xunit;

/// <summary>
/// The crashed run's console: picking which run holds a crash, and what the aggregator does with it.
/// <para>
/// The case behind all of it is romestead. The server aborted on an unhandled exception, the
/// supervisor restarted it a second later, and every console read afterwards showed a clean boot —
/// so the assistant reported no errors and no cause, truthfully about the run it could see and
/// uselessly about the one that mattered. The trace now reads the run that ended AT the crash.
/// </para>
/// </summary>
namespace TheKrystalShip.Kgsm.Assistant.Tests;

public class CrashRunSelectorTests
{
    private static readonly DateTimeOffset Crash = new(2026, 8, 11, 18, 16, 46, TimeSpan.Zero);

    private static ConsoleRunInfo Run(int index, DateTimeOffset? endedAt, bool current = false) =>
        new(index, current, endedAt);

    private static ConsoleRunInfo Crashed(int index, DateTimeOffset endedAt) =>
        new(index, Current: false, endedAt, ConsoleRunInfo.CrashedOutcome, ExitCode: 139);

    private static ConsoleRunInfo Stopped(int index, DateTimeOffset endedAt) =>
        new(index, Current: false, endedAt, "stopped");

    [Fact]
    public void PicksTheRunThatEndedJustBeforeTheCrashWasSeen()
    {
        // The real shape: the process printed its last line at 18:16:38, the supervisor noticed the
        // cgroup had emptied at 18:16:46, and the restart's run began after that. Eight seconds of
        // detection lag, so the two stamps never match exactly.
        var runs = new[]
        {
            Run(0, endedAt: null, current: true),                                       // the restart, still up
            Run(1, new DateTimeOffset(2026, 8, 11, 18, 16, 38, TimeSpan.Zero)),         // the crashed run
            Run(2, new DateTimeOffset(2026, 8, 11, 17, 46, 50, TimeSpan.Zero)),         // the run before it
        };

        CrashRunSelector.Select(runs, Crash).Should().Be(1);
    }

    [Fact]
    public void NeverPicksARunStillInProgress()
    {
        // A run that has not ended cannot be the run that ended at the crash — and it is the one
        // holding the clean boot that made this bug look like an absence of errors.
        var runs = new[] { Run(0, endedAt: null, current: true) };

        CrashRunSelector.Select(runs, Crash).Should().BeNull();
    }

    [Fact]
    public void FindsTheCrashedRunWhenNothingRestartedBehindIt()
    {
        // The supervisor gave up: nothing is running, and the crashed run's output is still at the
        // live path awaiting the next spawn's rotation. It reports as not-current with a real end,
        // so it is found the same way as any other.
        var runs = new[] { Run(0, new DateTimeOffset(2026, 8, 11, 18, 16, 44, TimeSpan.Zero)) };

        CrashRunSelector.Select(runs, Crash).Should().Be(0);
    }

    [Fact]
    public void UnderACrashLoop_PicksTheRunNearestThatCrash()
    {
        // Runs seconds apart. Each crash belongs to the run that ended nearest it, and specifically
        // to the one that ended BEFORE it — reaching forward would pick the restart that followed,
        // whose console is the clean boot this whole path exists to stop mistaking for evidence.
        var runs = new[]
        {
            Run(0, endedAt: null, current: true),
            Run(1, new DateTimeOffset(2026, 8, 11, 18, 16, 44, TimeSpan.Zero)),
            Run(2, new DateTimeOffset(2026, 8, 11, 18, 16, 20, TimeSpan.Zero)),
            Run(3, new DateTimeOffset(2026, 8, 11, 18, 15, 55, TimeSpan.Zero)),
        };

        CrashRunSelector.Select(runs, Crash).Should().Be(1);

        // The earlier crash in the loop resolves to ITS run, not to the later one 22s afterwards.
        CrashRunSelector.Select(runs, new DateTimeOffset(2026, 8, 11, 18, 16, 22, TimeSpan.Zero)).Should().Be(2);
    }

    [Fact]
    public void IgnoresARunThatEndedAfterTheCrashWasSeen()
    {
        // A later run cannot hold an earlier crash. Only clock skew is tolerated forward.
        var runs = new[] { Run(0, Crash.AddMinutes(4)) };

        CrashRunSelector.Select(runs, Crash).Should().BeNull();
    }

    [Fact]
    public void TheRunTheSupervisorMarkedCrashed_OutranksOneThatMerelyEndedNearer()
    {
        // An operator stopped the server and started it again seconds before the crash. Both runs
        // ended inside the window and the STOP ended nearer, so proximity alone hands back a console
        // that holds an orderly shutdown. The supervisor watched one of these processes fail; that
        // verdict is measured, and it wins.
        var runs = new[]
        {
            Run(0, endedAt: null, current: true),
            Stopped(1, new DateTimeOffset(2026, 8, 11, 18, 16, 45, TimeSpan.Zero)),
            Crashed(2, new DateTimeOffset(2026, 8, 11, 18, 16, 30, TimeSpan.Zero)),
        };

        CrashRunSelector.Select(runs, Crash).Should().Be(2);
    }

    [Fact]
    public void AmongSeveralCrashedRuns_TheNearestStillWins()
    {
        // Marking narrows the field; it does not replace the pairing. In a crash loop every run is
        // marked, and each crash still belongs to the run that ended nearest it.
        var runs = new[]
        {
            Run(0, endedAt: null, current: true),
            Crashed(1, new DateTimeOffset(2026, 8, 11, 18, 16, 44, TimeSpan.Zero)),
            Crashed(2, new DateTimeOffset(2026, 8, 11, 18, 16, 20, TimeSpan.Zero)),
        };

        CrashRunSelector.Select(runs, Crash).Should().Be(1);
        CrashRunSelector.Select(runs, new DateTimeOffset(2026, 8, 11, 18, 16, 22, TimeSpan.Zero))
            .Should().Be(2);
    }

    [Fact]
    public void AnUnclassifiedRunIsStillFoundByTime()
    {
        // Every run rotated before the supervisor kept a ledger reports "unknown", and unknown is not
        // "did not crash". Requiring the mark would make an absent record look like an absent crash
        // and lose the console entirely.
        var runs = new[]
        {
            Run(0, endedAt: null, current: true),
            Run(1, new DateTimeOffset(2026, 8, 11, 18, 16, 38, TimeSpan.Zero)),
        };

        runs[1].Outcome.Should().Be(ConsoleRunInfo.UnknownOutcome);
        CrashRunSelector.Select(runs, Crash).Should().Be(1);
    }

    [Fact]
    public void GivingUpCountsAsHavingCrashed()
    {
        // The supervisor stops retrying after enough consecutive failures and files the last run
        // under a different word. It is the same event — the process died on its own — and its
        // console is the one worth reading.
        var gaveUp = new ConsoleRunInfo(
            1, Current: false, new DateTimeOffset(2026, 8, 11, 18, 16, 30, TimeSpan.Zero),
            ConsoleRunInfo.GaveUpOutcome, ExitCode: 1);

        var runs = new[] { Stopped(0, new DateTimeOffset(2026, 8, 11, 18, 16, 45, TimeSpan.Zero)), gaveUp };

        CrashRunSelector.Select(runs, Crash).Should().Be(1);
    }

    [Fact]
    public void RefusesAPairingTooFarApartToBeEvidence()
    {
        // An hour between the last line and the observed exit is not detection lag. Pairing them
        // would attach a stack trace to a crash it has nothing to do with — worse than saying
        // nothing, because it looks like an answer.
        var runs = new[] { Run(0, Crash.AddHours(-1)) };

        CrashRunSelector.Select(runs, Crash).Should().BeNull();
    }
}

public class FatalConsoleOutputRuleTests
{
    private const string Instance = "romestead";
    private static readonly DateTimeOffset CrashTs = new(2026, 8, 11, 18, 16, 46, TimeSpan.Zero);

    private static readonly AuditEventRow CrashEvent =
        new("evt_1", CrashTs, "instance_crashed", Instance, Actor: "system:watchdog", Origin: "system");

    private static EventHistoryReading Timeline(params AuditEventRow[] rows) =>
        new(AuditReadState.Available, rows.OrderByDescending(r => r.Ts).ToList());

    private static ServerMetricsHistory NoMetrics() =>
        new(PerformanceState.MonitorUnavailable, "24h", null, new Dictionary<string, IReadOnlyList<MetricPoint>>());

    private static ToolResult<RootCauseData> Run(CrashConsole console, params AuditEventRow[] events) =>
        RootCauseAggregator.Run(
            Instance, "24h", Timeline(events.Length == 0 ? [CrashEvent] : events), NoMetrics(),
            health: null, healthUnavailableReason: "not read for this test", crashConsole: console);

    /// <summary>romestead's actual final lines, trimmed to the shape that matters.</summary>
    private static readonly string[] TheRealCrash =
    [
        "[CHAT][18:15:10.7249872] Job_Chat_UniqueFind <job:lumberjack*job:name> <male_name_64>",
        "Server is running behind by 21.064ms (over 1 update) (58.74 updates per second)",
        "[2026-08-11T18:16:36] 62.216.211.116:46994 is trying to connect",
        "Character 'Gingera' (Peer 3 - 62.216.211.116:46994) logged in with external id '', assigned to player id 4",
        "Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.",
        "   at CandideServer.Entities.ServerEntitySystemManager.UpdateLoadInAndOutOfOpenWorld(Single dt)",
        "   at CandideServer.Server.BaseServer.Run(MultiplayerConfiguration config)",
        "   at Server.Program.Main(String[] args)",
    ];

    [Fact]
    public void QuotesTheCrashedRunsLastWords_AtConfirmed()
    {
        var r = Run(new CrashConsole(CrashEvent, TheRealCrash, FactsState.Available));

        var best = r.Data.Findings[0];
        best.Signature.Should().Be(RootCauseSignature.FatalConsoleOutput);
        best.Confidence.Should().Be(Confidence.Confirmed);

        // The excerpt has to be IN the summary: the model is shown only that, and quoting evidence
        // it cannot see is exactly the fabrication this tool exists to avoid.
        r.Summary.Should().Contain("unhandled exception")
            .And.Contain("System.NullReferenceException")
            .And.Contain("UpdateLoadInAndOutOfOpenWorld");
        r.Summary.Should().NotContain("No known failure signature matched");

        best.ConsoleExcerpt.Should().NotBeNull();
        best.ConsoleExcerpt!.Should().Contain(l => l.Contains("Unhandled exception"));
    }

    [Fact]
    public void ReportsTheLastFatalLine_NotAnEarlierSurvivedOne()
    {
        // A long-lived server can log something alarming and carry on for hours. What killed it is
        // what it said last, so the scan runs backwards.
        string[] lines =
        [
            "Fatal error: could not load optional plugin 'foo' (continuing)",
            "Server ready...",
            "players connected: 4",
            "Unhandled exception. System.NullReferenceException: Object reference not set.",
        ];

        var r = Run(new CrashConsole(CrashEvent, lines, FactsState.Available));

        // The QUOTED line — what the finding says it signed off with — is the exception. The earlier
        // "Fatal error" still appears further down, because the excerpt is the run's whole tail and
        // trimming context out of it would hide what led up to the crash.
        r.Summary.Should().Contain("signed off with an unhandled exception: \"Unhandled exception.");
        r.Summary.Should().NotContain("signed off with a fatal error");
    }

    [Fact]
    public void OrdinaryErrorLinesDoNotCountAsFatal()
    {
        // Games log ERROR while perfectly healthy. Matching those would stamp Confirmed onto noise,
        // so unrecognised output falls through to the correlation instead.
        string[] lines =
        [
            "[ERROR] failed to load texture pack 'shiny'",
            "[ERROR] player inventory desync, resyncing",
            "Server stopped",
        ];

        var r = Run(new CrashConsole(CrashEvent, lines, FactsState.Available));

        var best = r.Data.Findings[0];
        best.Signature.Should().Be(RootCauseSignature.None);
        best.Confidence.Should().Be(Confidence.Possible);

        // Still surfaced — unrecognised is a lead to read, not nothing to report.
        r.Summary.Should().Contain("last lines it printed").And.Contain("failed to load texture pack");
        best.ConsoleExcerpt.Should().NotBeNull();
    }

    [Fact]
    public void ASignalDeathIsNamed_BecauseTheConsoleCannotShowIt()
    {
        // The exact hole this closes: a process killed from outside prints nothing on its way out, so
        // the console reads as a server that was fine and then stopped. The exit code is the only
        // evidence there is, and it says the process did not choose to exit.
        var r = Run(new CrashConsole(
            CrashEvent,
            ["Ready.", "HostServer_SavingGame", "HostServer_WorldSaved"],
            FactsState.Available,
            ExitCode: 137));

        r.Summary.Should().Contain("SIGKILL").And.Contain("exit 137")
            .And.Contain("rather than exiting on its own");
        // The causes are listed as mechanisms and explicitly not ranked. Naming one as the likely
        // cause is a diagnosis an exit code cannot support, and the reader will make it if the text
        // leaves room for it.
        r.Summary.Should().Contain("cannot tell them apart");
    }

    [Fact]
    public void ExitZeroIsReported_ButNeverAsACleanShutdown()
    {
        var r = Run(new CrashConsole(CrashEvent, ["some output"], FactsState.Available, ExitCode: 0));

        r.Summary.Should().Contain("not evidence of a clean shutdown");
    }

    [Fact]
    public void AnUnreadExitCodeSaysNothingAtAll()
    {
        // An honest unknown stays silent rather than being described as anything.
        var r = Run(new CrashConsole(CrashEvent, ["some output"], FactsState.Available, ExitCode: null));

        r.Summary.Should().NotContain("exited with code").And.NotContain("terminated by");
    }

    [Fact]
    public void AnUnreadableConsole_IsSaidToBeUnreadable_NotClean()
    {
        // The whole failure mode in one assertion: a gap in the evidence must never read as an
        // absence of errors.
        var r = Run(new CrashConsole(CrashEvent, [], FactsState.Unavailable));

        r.Data.CrashConsoleState.Should().Be(FactsState.Unavailable);
        r.Summary.Should().Contain("couldn't be read").And.Contain("not an absence of errors");
    }

    [Fact]
    public void NoCrashInTheWindow_ReadsExactlyAsBefore()
    {
        // The tool answers plenty of questions that have no crash in them; none of this may change
        // what those say.
        var started = new AuditEventRow(
            "evt_2", CrashTs.AddHours(-3), "instance_started", Instance, Actor: "heisen", Origin: "cli");

        var r = Run(CrashConsole.NoCrash, started);

        r.Data.Findings[0].Signature.Should().Be(RootCauseSignature.None);
        r.Summary.Should().Contain("No known failure signature matched");
        r.Summary.Should().NotContain("last lines");
        r.Data.Findings[0].ConsoleExcerpt.Should().BeNull();
    }

    [Fact]
    public void TheExcerptIsBounded_SoAChattyServerCannotFloodTheContext()
    {
        var noisy = Enumerable.Range(0, 400).Select(i => $"tick {i} " + new string('x', 500)).ToList();
        noisy.Add("Unhandled exception. System.NullReferenceException: boom");

        var r = Run(new CrashConsole(CrashEvent, noisy, FactsState.Available));

        var excerpt = r.Data.Findings[0].ConsoleExcerpt!;
        excerpt.Count.Should().BeLessThanOrEqualTo(18);
        excerpt.Should().OnlyContain(l => l.Length <= 221);
        // The bound keeps the END, which is where a crash is.
        excerpt.Last().Should().Contain("Unhandled exception");
    }
}
