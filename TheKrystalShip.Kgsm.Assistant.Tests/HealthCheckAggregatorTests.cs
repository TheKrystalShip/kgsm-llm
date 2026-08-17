using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Envelope;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic health synthesis is a pure function, so these run without mocks.
/// They pin the load-bearing rule: a deliberately stopped server is never a failure, and
/// an unreadable source <c>Skip</c>s rather than fabricating a pass.
/// </summary>
public class HealthCheckAggregatorTests
{
    /// <summary>A healthy running instance: running, no error logs, up to date, disk 26%.</summary>
    private static InstanceHealthSnapshot Healthy(
        bool running = true,
        IReadOnlyList<string>? logs = null,
        int logsRequested = 200,
        bool? updatesAvailable = false,
        string? current = "1.0.0",
        string? latest = null,
        HostDisk? disk = null,
        bool hasDisk = true,
        string? diskReason = null,
        bool? portsReachable = true,
        string? portsDetail = null,
        InstanceRestart? restart = null,
        bool hasRestart = true) =>
        new(
            Running: running,
            // A healthy running server HAS clean log output; the logs check now Skips on an
            // empty read (honest "nothing to scan"), so the baseline must carry a real line.
            RecentLogLines: logs ?? new[] { "2026-06-14 10:00:00 INFO server running" },
            // A diagnostic-grade request by default; the small-sample cases pass their own.
            RecentLogLinesRequested: logsRequested,
            UpdatesAvailable: updatesAvailable,
            CurrentVersion: current,
            LatestVersion: latest,
            // hasDisk:false models "host read failed" — distinct from a present-but-default disk.
            HostDisk: hasDisk ? (disk ?? new HostDisk(26, "916G", "649G")) : null,
            HostDiskUnavailableReason: diskReason,
            // A healthy running server's configured ports are active; a null models "not probed" (skip).
            PortsReachable: portsReachable,
            PortsDetail: portsDetail,
            // A healthy running server has been up a good while. hasRestart:false models "no run
            // history" — the stability check then skips rather than assuming a settled run.
            Restart: hasRestart
                ? (restart ?? new InstanceRestart(Now.AddDays(-3), "stopped", null))
                : null);

    /// <summary>Fixed "now", so a test's uptime wording cannot drift with the clock.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private static ToolResult<HealthData> Run(InstanceHealthSnapshot snapshot, string id) =>
        HealthCheckAggregator.Run(snapshot, id, Now);

    private static HealthCheck Check(ToolResult<HealthData> r, string name) =>
        r.Data.Checks.Single(c => c.Name == name);

    [Fact]
    public void Healthy_RunningInstance_AllPass()
    {
        var r = Run(Healthy(), "minecraft");

        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Total.Should().Be(6);
        r.Data.Passed.Should().Be(6);
        r.Data.Skipped.Should().Be(0);
        r.Tool.Should().Be(ResultCardKinds.Health);
        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Subject.Should().Be(new ResultRef(ResourceKind.Server, "minecraft"));
        r.Summary.Should().Contain("minecraft").And.Contain("healthy");
    }

    [Fact]
    public void Stopped_IsNeverFailure_AndSkipsLogScan()
    {
        // The §D5 proof: stateless KGSM can't know stopped is a crash, so it must not fail.
        var r = Run(Healthy(running: false), "factorio-test");

        Check(r, "liveness").State.Should().Be(CheckState.Pass);
        Check(r, "liveness").Severity.Should().Be(Severity.Info);
        Check(r, "logs").State.Should().Be(CheckState.Skip);
        // Ports also skip on a stopped server (nothing is bound) — a second honest skip.
        Check(r, "ports").State.Should().Be(CheckState.Skip);
        r.Data.Overall.Should().NotBe(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Skipped.Should().Be(3);
        r.Summary.Should().Contain("stopped").And.Contain("skipped");
    }

    [Fact]
    public void RunningWithErrorLogs_WarnsAndSamples()
    {
        var logs = new[]
        {
            "2026-06-14 10:00:00 INFO  server started",
            "2026-06-14 10:01:00 ERROR failed to bind port 25565",
            "2026-06-14 10:02:00 normal line",
        };

        var r = Run(Healthy(logs: logs), "minecraft");

        Check(r, "logs").State.Should().Be(CheckState.Warn);
        Check(r, "logs").Detail.Should().Contain("1 error line").And.Contain("failed to bind");
        r.Data.Overall.Should().Be(CheckState.Warn);
        r.Summary.Should().Contain("warnings");
    }

    [Fact]
    public void RunningWithATinySample_SkipsHonestly_NeverFakesClean()
    {
        // The romestead case: the server aborted on an unhandled exception, the watchdog restarted
        // it, and the health check sampled three lines of the FRESH run — clean, because the crash
        // was in the previous one. Three clean lines is not evidence of a clean run, so the check
        // must skip. Reporting "No errors in recent logs" here is what made a wrong answer sound
        // measured.
        var lines = new[] { "Server ready...", "Gingera is trying to connect", "player id 1" };

        var r = Run(
            Healthy(logs: lines, logsRequested: 3), "romestead");

        var logs = Check(r, "logs");
        logs.State.Should().Be(CheckState.Skip);
        logs.State.Should().NotBe(CheckState.Pass);
        logs.Detail.Should().Contain("3 line").And.Contain("too few");
        r.Summary.Should().NotContain("No errors since the server last started");
        r.Data.Overall.Should().Be(CheckState.Pass);   // a skip never fails the overall
        r.Data.Skipped.Should().Be(1);
    }

    [Fact]
    public void ALargeRequestAnsweredWithFewLines_IsTheWholeLog_AndStillPasses()
    {
        // The counterpart: asking for a diagnostic-grade sample and getting a short answer means
        // the server has only printed that much. That IS the whole log, so reading it clean is
        // real evidence and must not be downgraded to a skip alongside the keyhole case.
        var lines = new[] { "Starting server", "Loading world 'tks'", "Server ready..." };

        var r = Run(
            Healthy(logs: lines, logsRequested: 200), "romestead");

        var logs = Check(r, "logs");
        logs.State.Should().Be(CheckState.Pass);
        logs.Detail.Should().Contain("No errors since the server last started");
    }

    [Fact]
    public void ATinySampleContainingAnError_StillSkips_RatherThanWarnOnAKeyhole()
    {
        // Symmetry: the sample is too small to conclude anything, in EITHER direction. A three-line
        // probe that happens to catch an ERROR says nothing about how many there were, and the
        // tally's "1 error line" would be a number the sample cannot support.
        var lines = new[] { "INFO ok", "ERROR failed to bind port 25565", "INFO ok" };

        var r = Run(
            Healthy(logs: lines, logsRequested: 3), "minecraft");

        Check(r, "logs").State.Should().Be(CheckState.Skip);
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void RunningWithNoLogs_SkipsHonestly_NeverFakesClean()
    {
        // Honesty: a running instance with NO recent log lines has nothing to scan, so the
        // check must Skip ("couldn't read") — asserting "no errors" from zero evidence would
        // be a fabricated clean bill (the kgsm recent_logs=[] bug this guards against).
        var r = Run(Healthy(logs: Array.Empty<string>()), "factorio-test");

        var logs = Check(r, "logs");
        logs.State.Should().Be(CheckState.Skip);
        logs.State.Should().NotBe(CheckState.Pass);
        logs.Detail.Should().Contain("No recent log output");
        r.Data.Overall.Should().Be(CheckState.Pass);   // a skip never fails the overall
        r.Data.Skipped.Should().Be(1);
        r.Data.Passed.Should().Be(5);                  // liveness + stability + updates + disk + ports
    }

    [Fact]
    public void UpdateAvailable_WarnsWithVersions()
    {
        var r = Run(
            Healthy(updatesAvailable: true, current: "1.20.1", latest: "1.20.4"), "minecraft");

        var updates = Check(r, "updates");
        updates.State.Should().Be(CheckState.Warn);
        updates.Severity.Should().Be(Severity.Update);
        updates.Detail.Should().Contain("1.20.1").And.Contain("1.20.4");
        r.Summary.Should().Contain("Update available");
    }

    [Fact]
    public void UpdateStatusUnknown_Skips_NeverGuesses()
    {
        var r = Run(Healthy(updatesAvailable: null), "minecraft");

        Check(r, "updates").State.Should().Be(CheckState.Skip);
        r.Data.Skipped.Should().Be(1);
        // A null update status must not drag the verdict down.
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Theory]
    [InlineData(85, CheckState.Warn)]
    [InlineData(90, CheckState.Warn)]
    [InlineData(95, CheckState.Fail)]
    [InlineData(99, CheckState.Fail)]
    public void Disk_ThresholdsHeadroom(int usedPercent, CheckState expected)
    {
        var r = Run(
            Healthy(disk: new HostDisk(usedPercent, "916G", "10G")), "minecraft");

        Check(r, "disk").State.Should().Be(expected);
        r.Data.Overall.Should().Be(expected); // disk is the only non-pass check
    }

    [Fact]
    public void Disk_Unavailable_Skips_WithReason_NeverFakesZero()
    {
        var r = Run(
            Healthy(hasDisk: false, diskReason: "monitor offline"), "minecraft");

        var disk = Check(r, "disk");
        disk.State.Should().Be(CheckState.Skip);
        disk.Detail.Should().Contain("monitor offline");
        disk.Detail.Should().NotContain("0%");
        r.Data.Skipped.Should().Be(1);
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void Disk_PercentUnparsed_Skips()
    {
        var r = Run(
            Healthy(disk: new HostDisk(null, "916G", "649G")), "minecraft");

        Check(r, "disk").State.Should().Be(CheckState.Skip);
    }

    [Fact]
    public void Ports_ReachableWhileRunning_Passes()
    {
        var r = Run(Healthy(portsReachable: true), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Pass);
        ports.Severity.Should().Be(Severity.Success);
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void Ports_UnreachableWhileRunning_Warns_NotFail()
    {
        // A running server whose ports aren't bound is a warning (it may still be starting), never a
        // hard fail — the honest middle ground.
        var r = Run(Healthy(portsReachable: false), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Warn);
        ports.State.Should().NotBe(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Warn);
    }

    [Fact]
    public void Ports_NotProbed_Skips_WithReason_NeverFabricatesPass()
    {
        // null reachability (no ports configured / probe failed) must Skip, never assert reachable.
        var r = Run(
            Healthy(portsReachable: null, portsDetail: "no ports configured"), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Skip);
        ports.State.Should().NotBe(CheckState.Pass);
        ports.Detail.Should().Contain("no ports configured");
        r.Data.Overall.Should().Be(CheckState.Pass); // a skip never fails the overall
    }

    [Fact]
    public void Ports_StoppedServer_Skips_WithoutProbing()
    {
        // Even with a stale true, a stopped server's ports check skips (binding is meaningless stopped).
        var r = Run(Healthy(running: false, portsReachable: true), "factorio-test");

        Check(r, "ports").State.Should().Be(CheckState.Skip);
    }

    [Fact]
    public void Overall_IsWorstNonSkipCheck()
    {
        // Running + error logs (warn) + disk critically full (fail) → overall fail.
        var logs = new[] { "FATAL out of memory" };
        var r = Run(
            Healthy(logs: logs, disk: new HostDisk(98, "916G", "1G")), "minecraft");

        Check(r, "logs").State.Should().Be(CheckState.Warn);
        Check(r, "disk").State.Should().Be(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Fail);
        r.Summary.Should().Contain("problems");
    }

    // --- Stability: a server that is up again is not the same as a server that is well ------------

    private static InstanceHealthSnapshot AfterRestart(
        TimeSpan ago, string previousOutcome, int? exit = null, IReadOnlyList<string>? previousLines = null) =>
        Healthy(restart: new InstanceRestart(Now - ago, previousOutcome, exit, previousLines));

    [Fact]
    public void ARecentCrashRestart_IsNotReportedAsHealthy()
    {
        // The failure this check exists for. Every other check reads the CURRENT run: the log sample
        // starts where the run started, so a server that aborted and came back is examined entirely
        // after the fact and passes clean. Without this, the crash is in none of the evidence and so
        // in none of the answer — "healthy, no errors" ten minutes after it went down.
        var r = Run(AfterRestart(TimeSpan.FromMinutes(10), ConsoleRunInfo.CrashedOutcome, exit: 139), "romestead");

        r.Data.Overall.Should().Be(CheckState.Warn);
        r.Summary.Should().NotContain("romestead: healthy.");
        Check(r, "stability").State.Should().Be(CheckState.Warn);
        r.Summary.Should().Contain("Restarted 10 minutes ago after a crash").And.Contain("exit 139");

        // And the reason the clean log scan is not reassurance is stated, not left to be inferred.
        r.Summary.Should().Contain("this run's logs begin after it");
    }

    [Fact]
    public void TheLogScanNeverClaimsMoreThanTheRunItRead()
    {
        // "No errors in recent logs" reads as a statement about the server. It is only ever about the
        // stretch since it last started, and after a restart those are very different claims — so the
        // wording is scoped whether or not anything restarted recently.
        var r = Run(Healthy(), "minecraft");

        Check(r, "logs").Detail.Should().Be("No errors since the server last started.");
    }

    [Fact]
    public void AnOldCrashIsNotAWarning_AndTheUptimeIsReported()
    {
        // A server that has held for hours is demonstrating the stability the supervisor credited it
        // with long ago. Warning forever would make the check noise, and noise gets ignored.
        var r = Run(AfterRestart(TimeSpan.FromHours(9), ConsoleRunInfo.CrashedOutcome, exit: 139), "romestead");

        r.Data.Overall.Should().Be(CheckState.Pass);
        Check(r, "stability").Detail.Should().Be("Up 9 hours since the last restart.");
    }

    [Fact]
    public void ARecentDeliberateRestart_IsNotAWarning()
    {
        // Somebody restarting a server is somebody doing their job. Warning on it would train a
        // reader to discount the warning that matters.
        var r = Run(AfterRestart(TimeSpan.FromMinutes(5), "stopped"), "minecraft");

        r.Data.Overall.Should().Be(CheckState.Pass);
        Check(r, "stability").Detail.Should().Contain("after a deliberate stop rather than a failure");
    }

    [Fact]
    public void AnUnrecordedPreviousEnding_IsSaidToBeUnknown_AndDoesNotWarn()
    {
        // Not knowing how a run ended is not evidence that it failed — warning on it would invent a
        // crash. Nor is it evidence it ended cleanly, which is why it is said out loud.
        var r = Run(AfterRestart(TimeSpan.FromMinutes(3), ConsoleRunInfo.UnknownOutcome), "romestead");

        r.Data.Overall.Should().Be(CheckState.Pass);
        Check(r, "stability").Detail.Should()
            .Contain("never recorded").And.Contain("not the same as it having ended cleanly");
    }

    [Fact]
    public void NoRunHistory_Skips_RatherThanAssumingAStableRun()
    {
        // An unreadable run list must not read as "nothing has restarted" — the same fabrication the
        // rest of this aggregator refuses everywhere else.
        var r = Run(Healthy(hasRestart: false), "minecraft");

        var stability = Check(r, "stability");
        stability.State.Should().Be(CheckState.Skip);
        stability.Detail.Should().Contain("unknown");
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void AStoppedInstance_SkipsStability()
    {
        // Uptime is not a question a stopped server has an answer to.
        var r = Run(Healthy(running: false), "factorio-test");

        Check(r, "stability").State.Should().Be(CheckState.Skip);
    }

    [Fact]
    public void GivingUpCountsAsACrash()
    {
        var r = Run(AfterRestart(TimeSpan.FromMinutes(2), ConsoleRunInfo.GaveUpOutcome, exit: 1), "romestead");

        Check(r, "stability").State.Should().Be(CheckState.Warn);
    }

    [Fact]
    public void ARecentCrashQuotesWhatTheCrashedRunSaid()
    {
        // The point of carrying the previous run's output at all: a report that names a crash and
        // withholds the line explaining it costs the reader a second question about the very thing
        // they were already asking.
        string[] lastWords =
        [
            "Character 'Gingera' logged in with player id 4",
            "Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.",
            "   at CandideServer.Entities.ServerEntitySystemManager.UpdateLoadInAndOutOfOpenWorld(Single dt)",
        ];

        var r = Run(AfterRestart(
            TimeSpan.FromMinutes(4), ConsoleRunInfo.CrashedOutcome, exit: 134, previousLines: lastWords), "romestead");

        r.Data.Overall.Should().Be(CheckState.Warn);
        r.Summary.Should().Contain("signed off with an unhandled exception")
            .And.Contain("System.NullReferenceException");
    }

    [Fact]
    public void OutputThatAnnouncesNothing_IsNotQuotedAsIfItWereTheCause()
    {
        // A server killed from outside prints routine chatter and then stops. Quoting its last line
        // beside a crash invites it to be read as the cause — "Server ready..." is not why it died.
        // Saying it announced nothing points away from an application fault, which is the real signal.
        string[] routine = ["Server ready...", "Saving game...", "HostServer_WorldSaved"];

        var r = Run(AfterRestart(
            TimeSpan.FromMinutes(1), ConsoleRunInfo.CrashedOutcome, exit: 137, previousLines: routine), "romestead");

        r.Summary.Should().Contain("printed nothing that announces a crash");
        r.Summary.Should().NotContain("HostServer_WorldSaved").And.NotContain("Server ready");
    }

    [Fact]
    public void AnUnreadableCrashedRun_SaysSo_RatherThanImplyingItWasSilent()
    {
        // A read that failed and a run that printed nothing are different facts, and the second is
        // evidence while the first is a gap.
        var r = Run(AfterRestart(TimeSpan.FromMinutes(2), ConsoleRunInfo.CrashedOutcome, exit: 1), "romestead");

        r.Summary.Should().Contain("could not be read");
        r.Summary.Should().NotContain("printed nothing that announces a crash");
    }

    [Fact]
    public void TheQuoteIsTheLastFatalLine_NotAnEarlierSurvivedOne()
    {
        // A long-lived server logs something alarming and carries on for hours. What killed it is
        // what it said last, so the scan runs backwards — the same rule trace_root_cause uses,
        // because they now share one table.
        string[] lines =
        [
            "Fatal error: could not load optional plugin 'foo' (continuing)",
            "Server ready...",
            "Unhandled exception. System.NullReferenceException: boom",
        ];

        var r = Run(AfterRestart(
            TimeSpan.FromMinutes(2), ConsoleRunInfo.CrashedOutcome, previousLines: lines), "romestead");

        r.Summary.Should().Contain("signed off with an unhandled exception");
        r.Summary.Should().NotContain("signed off with a fatal error");
    }

    [Fact]
    public void AnOldCrashQuotesNothing_BecauseItIsNoLongerTheAnswer()
    {
        // Past the window the check does not warn, so there is nothing for last words to qualify.
        string[] lastWords = ["Unhandled exception. System.NullReferenceException: boom"];

        var r = Run(AfterRestart(
            TimeSpan.FromHours(9), ConsoleRunInfo.CrashedOutcome, previousLines: lastWords), "romestead");

        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Summary.Should().NotContain("NullReferenceException");
    }
}
