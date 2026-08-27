using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.RootCause;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The <c>trace_root_cause</c> capstone aggregator is a pure, deterministic function over three
/// fixture-friendly inputs — an event timeline, a metrics window, and a health snapshot — so these
/// run without mocks. One test per rules-table entry, plus the two honesty guarantees: no signature
/// matched still produces a ranked correlation (never a guessed cause), and a source that could not
/// be read degrades gracefully (never a fabricated pass).
/// </summary>
public class RootCauseAggregatorTests
{
    private const string Instance = "factorio-test";
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private static AuditEventRow Ev(string type, DateTimeOffset ts, string id) =>
        new(id, ts, type, Instance, Actor: "heisen", Origin: "cli");

    private static EventHistoryReading Events(params AuditEventRow[] rows) =>
        // The real port contract is ts-DESC (most-recent-first); mirror it so the correlation
        // fallback (which trusts that ordering) is exercised the same way it is in production.
        new(AuditReadState.Available, rows.OrderByDescending(r => r.Ts).ToList());

    private static ServerMetricsHistory UnavailableMetrics(string range = "24h") =>
        new(PerformanceState.MonitorUnavailable, range, null, new Dictionary<string, IReadOnlyList<MetricPoint>>());

    private static ServerMetricsHistory LiveMetrics(string range = "24h") =>
        new(PerformanceState.Live, range, "raw", new Dictionary<string, IReadOnlyList<MetricPoint>>
        {
            ["cpuPctCore"] = new[] { new MetricPoint(T0, 40), new MetricPoint(T0.AddMinutes(5), 80) },
            ["memBytes"] = new[] { new MetricPoint(T0, 500_000_000), new MetricPoint(T0.AddMinutes(5), 700_000_000) },
        });

    private static InstanceHealthSnapshot Healthy(
        bool running = true, HostDisk? disk = null) =>
        new(
            Running: running,
            RecentLogLines: running ? new[] { "INFO nominal" } : Array.Empty<string>(),
            RecentLogLinesRequested: 200,
            UpdatesAvailable: false,
            CurrentVersion: "1.0.0",
            LatestVersion: null,
            HostDisk: disk ?? new HostDisk(30, "100G", "70G"),
            HostDiskUnavailableReason: null);

    // --- Rule 1: port conflict / bind failure -------------------------------------------------------

    [Fact]
    public void PortConflict_StartThenCrashWithNoReady_MatchesAtLikely()
    {
        var events = Events(
            Ev("server.started", T0, "e1"),
            Ev("server.crashed", T0.AddMinutes(1), "e2"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        var top = r.Data.Findings[0];
        top.Signature.Should().Be(RootCauseSignature.PortConflictOrBindFailure);
        top.Confidence.Should().Be(Confidence.Likely);
        top.Events.Select(e => e.Id).Should().Contain(new[] { "e1", "e2" });
        r.Confidence.Should().Be(Confidence.Likely);
        r.Summary.Should().Contain("port conflict");
    }

    [Fact]
    public void PortConflict_StartThenReadyThenCrash_DoesNotMatch()
    {
        // Reached "ready" before crashing — a normal running server that later crashed, not a
        // failed-to-bind pattern.
        var events = Events(
            Ev("server.started", T0, "e1"),
            Ev("server.ready", T0.AddSeconds(30), "e2"),
            Ev("server.crashed", T0.AddMinutes(2), "e3"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.PortConflictOrBindFailure);
    }

    [Fact]
    public void PortConflict_CrashOutsideWindow_DoesNotMatch()
    {
        var events = Events(
            Ev("server.started", T0, "e1"),
            Ev("server.crashed", T0.AddHours(1), "e2")); // well outside the 3-minute startup window

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.PortConflictOrBindFailure);
    }

    // --- Rule 2: update-mid-run crash loop ----------------------------------------------------------

    [Fact]
    public void UpdateCrashLoop_SingleCrashAfterUpdate_MatchesAtLikely()
    {
        var events = Events(
            Ev("server.update.finished", T0, "u1"),
            Ev("server.crashed", T0.AddMinutes(5), "c1"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        var finding = r.Data.Findings.Single(f => f.Signature == RootCauseSignature.UpdateMidRunCrashLoop);
        finding.Confidence.Should().Be(Confidence.Likely);
        finding.Explanation.Should().Contain("once");
    }

    [Fact]
    public void UpdateCrashLoop_TwoCrashesAfterUpdate_MatchesAtConfirmed()
    {
        var events = Events(
            Ev("server.update.finished", T0, "u1"),
            Ev("server.crashed", T0.AddMinutes(2), "c1"),
            Ev("server.crashed", T0.AddMinutes(6), "c2"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        var top = r.Data.Findings[0];
        top.Signature.Should().Be(RootCauseSignature.UpdateMidRunCrashLoop);
        top.Confidence.Should().Be(Confidence.Confirmed);
        top.Explanation.Should().Contain("crash loop");
        r.Confidence.Should().Be(Confidence.Confirmed);
    }

    [Fact]
    public void UpdateCrashLoop_CrashFarAfterUpdate_DoesNotMatch()
    {
        var events = Events(
            Ev("server.update.finished", T0, "u1"),
            Ev("server.crashed", T0.AddHours(2), "c1")); // outside the 15-minute window

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.UpdateMidRunCrashLoop);
    }

    // --- Rule 3: disk full ---------------------------------------------------------------------------

    [Fact]
    public void DiskFull_CriticalDiskPlusFailureEvent_MatchesAtConfirmed()
    {
        var events = Events(Ev("server.deploy.failed", T0, "d1"));
        var health = Healthy(disk: new HostDisk(97, "100G", "2G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        var top = r.Data.Findings[0];
        top.Signature.Should().Be(RootCauseSignature.DiskFull);
        top.Confidence.Should().Be(Confidence.Confirmed);
        top.Events.Should().ContainSingle(e => e.Id == "d1");
        top.HealthChecks.Should().Contain(c => c.Name == "disk" && c.State == CheckState.Fail);
    }

    [Fact]
    public void DiskFull_CriticalDiskButNoFailureEvent_DoesNotMatch()
    {
        var events = Events(Ev("server.started", T0, "e1"));
        var health = Healthy(disk: new HostDisk(97, "100G", "2G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.DiskFull);
    }

    [Fact]
    public void DiskFull_FailureEventButDiskHealthy_DoesNotMatch()
    {
        var events = Events(Ev("server.deploy.failed", T0, "d1"));
        var health = Healthy(disk: new HostDisk(40, "100G", "60G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.DiskFull);
    }

    // --- Rule 4: split-brain --------------------------------------------------------------------------

    [Fact]
    public void SplitBrain_StartedButLiveSnapshotSaysNotRunning_MatchesAtConfirmed()
    {
        var events = Events(Ev("server.started", T0, "e1"));
        var health = Healthy(running: false);

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        var top = r.Data.Findings[0];
        top.Signature.Should().Be(RootCauseSignature.SplitBrain);
        top.Confidence.Should().Be(Confidence.Confirmed);
        top.Events.Should().ContainSingle(e => e.Id == "e1");
    }

    [Fact]
    public void SplitBrain_StoppedThenLiveSnapshotSaysNotRunning_DoesNotMatch()
    {
        // The event log and live state AGREE (both say not running) — no split.
        var events = Events(
            Ev("server.started", T0, "e1"),
            Ev("server.stopped", T0.AddMinutes(10), "e2"));
        var health = Healthy(running: false);

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.SplitBrain);
    }

    [Fact]
    public void SplitBrain_StartedAndLiveSnapshotSaysRunning_DoesNotMatch()
    {
        var events = Events(Ev("server.started", T0, "e1"));
        var health = Healthy(running: true);

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.SplitBrain);
    }

    // --- No signature matched: honest correlation, never a guessed cause -----------------------------

    [Fact]
    public void NothingMatches_ReturnsRankedCorrelation_AtPossible_NeverAGuessedCause()
    {
        var events = Events(
            Ev("backup.created", T0, "b1"),
            Ev("player.joined", T0.AddMinutes(1), "p1"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        r.Data.Findings.Should().ContainSingle();
        var only = r.Data.Findings[0];
        only.Signature.Should().Be(RootCauseSignature.None);
        only.Confidence.Should().Be(Confidence.Possible);
        only.Explanation.Should().Contain("No known failure signature matched");
        // Player noise is skipped in favor of the backup event when something else is available.
        only.Events.Should().ContainSingle(e => e.Id == "b1");
        r.Confidence.Should().Be(Confidence.Possible);
    }

    [Fact]
    public void NoEventsAtAll_ReturnsHonestNothingToCorrelate()
    {
        var r = RootCauseAggregator.Run(Instance, "24h", Events(), UnavailableMetrics(), Healthy());

        var only = r.Data.Findings[0];
        only.Signature.Should().Be(RootCauseSignature.None);
        only.Explanation.Should().Contain("nothing to correlate");
        only.Events.Should().BeEmpty();
    }

    // --- Source-unavailable graceful degradation ------------------------------------------------------

    [Fact]
    public void EventTimelineUnavailable_NoRuleCanRun_HonestUnavailableFinding()
    {
        var events = new EventHistoryReading(AuditReadState.JournalUnavailable, Array.Empty<AuditEventRow>());

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), Healthy());

        r.Data.EventState.Should().Be(AuditReadState.JournalUnavailable);
        var only = r.Data.Findings.Should().ContainSingle().Subject;
        only.Signature.Should().Be(RootCauseSignature.None);
        only.Confidence.Should().Be(Confidence.Possible);
        only.Explanation.Should().Contain("unavailable").And.Contain("isn't evidence nothing happened");
        r.Confidence.Should().Be(Confidence.Possible);
    }

    [Fact]
    public void HealthUnavailable_DiskAndSplitBrainRulesSkip_ButEventOnlyRulesStillRun()
    {
        // A port-conflict-shaped timeline with NO health snapshot: the event-only rule still fires;
        // disk-full/split-brain (which both need the snapshot) are silently un-evaluatable, never
        // faked into a false negative OR a false positive.
        var events = Events(
            Ev("server.started", T0, "e1"),
            Ev("server.crashed", T0.AddMinutes(1), "e2"));

        var r = RootCauseAggregator.Run(
            Instance, "24h", events, UnavailableMetrics(), health: null, healthUnavailableReason: "monitor offline");

        r.Data.HealthAvailable.Should().BeFalse();
        r.Data.HealthUnavailableReason.Should().Be("monitor offline");
        r.Data.Findings.Should().NotContain(f => f.Signature == RootCauseSignature.DiskFull || f.Signature == RootCauseSignature.SplitBrain);
        r.Data.Findings[0].Signature.Should().Be(RootCauseSignature.PortConflictOrBindFailure);
        // No health checks to attach when the snapshot never arrived.
        r.Data.Findings[0].HealthChecks.Should().BeEmpty();
    }

    [Fact]
    public void MetricsUnavailable_DoesNotBlockAnyRule_FactsStayEmpty()
    {
        var events = Events(Ev("server.deploy.failed", T0, "d1"));
        var health = Healthy(disk: new HostDisk(97, "100G", "2G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.MetricsState.Should().Be(PerformanceState.MonitorUnavailable);
        r.Data.Findings[0].Signature.Should().Be(RootCauseSignature.DiskFull);
        r.Data.Findings[0].Metrics.Should().BeEmpty();
    }

    [Fact]
    public void MetricsLive_AttachesAvgPeakFactsAsContext_NeverGatingARule()
    {
        var events = Events(Ev("server.deploy.failed", T0, "d1"));
        var health = Healthy(disk: new HostDisk(97, "100G", "2G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, LiveMetrics(), health);

        r.Data.Findings[0].Metrics.Should().Contain(m => m.Metric == "cpuPctCore" && m.Detail.Contains("60"));
        r.Data.Findings[0].Metrics.Should().Contain(m => m.Metric == "memBytes");
    }

    // --- Ranking: multiple matches sort Confirmed before Likely --------------------------------------

    [Fact]
    public void MultipleMatches_RankConfirmedBeforeLikely()
    {
        var events = Events(
            Ev("server.deploy.failed", T0, "d1"),                              // disk-full (Confirmed)
            Ev("server.update.finished", T0.AddHours(1), "u1"),                // crash-loop (Likely)
            Ev("server.crashed", T0.AddHours(1).AddMinutes(5), "c1"));
        var health = Healthy(disk: new HostDisk(96, "100G", "3G"));

        var r = RootCauseAggregator.Run(Instance, "24h", events, UnavailableMetrics(), health);

        r.Data.Findings.Should().HaveCountGreaterThanOrEqualTo(2);
        r.Data.Findings[0].Signature.Should().Be(RootCauseSignature.DiskFull);
        r.Data.Findings[0].Confidence.Should().Be(Confidence.Confirmed);
        r.Data.Findings.Should().Contain(f => f.Signature == RootCauseSignature.UpdateMidRunCrashLoop && f.Confidence == Confidence.Likely);
    }

    // --- Envelope shape -------------------------------------------------------------------------------

    [Fact]
    public void Result_CarriesTheRightToolAndSubject()
    {
        var r = RootCauseAggregator.Run(Instance, "24h", Events(), UnavailableMetrics(), Healthy());

        r.Tool.Should().Be(ResultCardKinds.RootCause);
        r.Subject.Should().Be(new ResultRef(ResourceKind.Server, Instance));
        r.Data.Instance.Should().Be(Instance);
        r.Data.Range.Should().Be("24h");
    }
}
