using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Consoles;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.RootCause;

/// <summary>
/// The deterministic <c>trace_root_cause</c> synthesis (locked
/// 2026-06-11): composes the recent engine-event timeline, a metrics window, and a health/status
/// snapshot for ONE instance, and runs a fixed rules table of known KGSM failure signatures over
/// them. Pure and I/O-free — every fetch happens in the dispatcher (in parallel, one call per
/// source), so this is unit-testable without mocks and makes NO nested model call: the model only
/// ever narrates a finding this class already computed, exactly like <see cref="Health.HealthCheckAggregator"/>
/// and <see cref="Metrics.PerformanceReport"/>.
/// <para>
/// **Governing rule:** the model is a router + narrator, never a reasoner. Causal inference
/// on a small local model is exactly the plausible-but-wrong failure the old kgsm-api was scrapped
/// for (ecosystem "never fabricate" rule) — so every finding here is either a mechanical pattern
/// match over measured facts, or an honest "nothing matched" correlation at
/// <see cref="Confidence.Possible"/>. Nothing here ever guesses a cause.
/// </para>
/// <para>
/// **Per-source degradation is independent.** Every rule declares which sources it needs; a source
/// that could not be read (<see cref="AuditReadState.JournalUnavailable"/>, an unreadable-journal
/// metrics read, or a failed health snapshot) simply makes that rule unevaluatable — it is skipped,
/// never treated as a negative/zero/"no problem" reading. If the event timeline itself is
/// unavailable, NO rule can run (every rule needs at least one event) and the result says so
/// honestly rather than silently returning an empty finding list.
/// </para>
/// </summary>
public static class RootCauseAggregator
{
    // A start/restart that crashes or fails within this window without ever reaching "ready" reads
    // as a bind/port-conflict-shaped failure (kgsm's event log has no dedicated bind-failure reason,
    // so this pattern is reported as Likely, never Confirmed).
    private static readonly TimeSpan StartupWindow = TimeSpan.FromMinutes(3);

    // A crash landing within this window after an update finished reads as update-triggered.
    private static readonly TimeSpan UpdateCrashWindow = TimeSpan.FromMinutes(15);

    private static readonly IReadOnlySet<string> UpdateEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "instance_update_finished", "instance_updated", "instance_version_updated",
    };

    private static readonly IReadOnlySet<string> DiskFailureAdjacentEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "instance_deploy_failed", "instance_download_failed", "instance_uninstall_failed",
    };

    private static readonly IReadOnlySet<string> RunningImpliesEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "instance_started", "instance_restarted", "instance_ready",
    };

    private const int MaxSalientCorrelation = 5;
    private const int MaxEvidenceEventsPerFinding = 5;

    /// <summary>
    /// Runs the deterministic sweep. Always returns a result — <see cref="RootCauseData.Findings"/>
    /// is never empty: a source-unavailable or nothing-matched outcome still produces exactly one
    /// honest correlation/unavailable finding at <see cref="Confidence.Possible"/>.
    /// </summary>
    /// <param name="instance">The resolved instance name (the result's subject).</param>
    /// <param name="range">The normalized window/range that was queried (e.g. <c>24h</c>).</param>
    /// <param name="events">The neutral event-timeline read from the engine's journal.</param>
    /// <param name="metrics">The neutral metrics-window read from the monitor.</param>
    /// <param name="health">The neutral health-snapshot inputs, or <see langword="null"/> when the
    /// snapshot could not be read (see <paramref name="healthUnavailableReason"/>).</param>
    /// <param name="healthUnavailableReason">Why the health snapshot is absent; ignored when
    /// <paramref name="health"/> is non-null.</param>
    /// <param name="crashConsole">What the run that ended at the crash printed, and how it exited.
    /// Null is the same as <see cref="CrashConsole.NoCrash"/> — nothing to read for, which is distinct
    /// from a read that failed.</param>
    public static ToolResult<RootCauseData> Run(
        string instance,
        string range,
        EventHistoryReading events,
        ServerMetricsHistory metrics,
        InstanceHealthSnapshot? health,
        string? healthUnavailableReason = null,
        CrashConsole? crashConsole = null)
    {
        var console = crashConsole ?? CrashConsole.NoCrash;
        var metricFacts = BuildMetricFacts(metrics);
        var healthChecks = health is not null
            ? HealthCheckAggregator.Run(health, instance).Data.Checks
            : Array.Empty<HealthCheck>();

        var findings = events.State == AuditReadState.JournalUnavailable
            ? new[] { UnavailableFinding(instance, metricFacts, healthChecks) }
            : EvaluateRules(instance, events.Events, health, healthChecks, metricFacts, console);

        var data = new RootCauseData(
            instance, range, events.State, metrics.State,
            HealthAvailable: health is not null,
            HealthUnavailableReason: health is null ? healthUnavailableReason : null,
            Findings: findings,
            EventsConsidered: events.Events.Count,
            CrashConsoleState: console.State);

        var best = findings[0];
        return new ToolResult<RootCauseData>(
            Tool: LlmTools.TraceRootCause,
            Confidence: best.Confidence,
            Subject: new ResultRef(ResourceKind.Server, instance),
            Summary: best.Explanation,
            Data: data);
    }

    // --- Rules table (evaluated in this priority order for tie-breaking) ---------------------------

    private static IReadOnlyList<RootCauseFinding> EvaluateRules(
        string instance,
        IReadOnlyList<AuditEventRow> events,
        InstanceHealthSnapshot? health,
        IReadOnlyList<HealthCheck> healthChecks,
        IReadOnlyList<MetricFact> metricFacts,
        CrashConsole console)
    {
        // Rules read most-recent-first input ts-ascending, since "shortly after" pairing scans forward.
        var ascending = events.OrderBy(e => e.Ts).ToList();

        var matched = new List<RootCauseFinding>();

        if (MatchFatalConsoleOutput(instance, console, metricFacts, healthChecks) is { } fatal)
            matched.Add(fatal);

        if (MatchPortConflict(instance, ascending, metricFacts, healthChecks) is { } portConflict)
            matched.Add(portConflict);

        if (MatchUpdateCrashLoop(instance, ascending, metricFacts, healthChecks) is { } crashLoop)
            matched.Add(crashLoop);

        if (health is not null && MatchDiskFull(instance, ascending, healthChecks, metricFacts) is { } diskFull)
            matched.Add(diskFull);

        if (health is not null && MatchSplitBrain(instance, ascending, health, healthChecks, metricFacts) is { } splitBrain)
            matched.Add(splitBrain);

        if (matched.Count > 0)
            return Rank(matched);

        return new[] { BuildCorrelation(instance, events, metricFacts, healthChecks, console) };
    }

    /// <summary>
    /// What the crashed run printed last. The strongest evidence available for a crash and the only
    /// rule reading the failing process's own words rather than the supervisor's account of it.
    /// <para>
    /// <b>This reports, it does not diagnose.</b> The claim is "these are the last lines the process
    /// printed before it exited, and one of them says it was dying" — every part of which is
    /// measured, which is why it carries <see cref="Confidence.Confirmed"/> where the inferential
    /// rules cannot. It never says the quoted line CAUSED the crash; a reader draws that, with the
    /// text in front of them.
    /// </para>
    /// <para>
    /// A recognised signature is what separates this from the correlation fallback, and the list is
    /// deliberately narrow: phrases a runtime emits only while terminating. A plain <c>ERROR</c> line
    /// is not one — games log those all day while perfectly healthy, and matching them would attach
    /// <see cref="Confidence.Confirmed"/> to noise. Unrecognised output is still surfaced, as the
    /// correlation's excerpt, at correlation strength.
    /// </para>
    /// </summary>
    private static RootCauseFinding? MatchFatalConsoleOutput(
        string instance, CrashConsole console,
        IReadOnlyList<MetricFact> metricFacts, IReadOnlyList<HealthCheck> healthChecks)
    {
        if (!console.HasOutput)
            return null;

        var hit = CrashOutput.FindFatalSignature(console.Lines);
        if (hit is null)
            return null;

        var excerpt = CrashOutput.TailExcerpt(console.Lines);
        var crash = console.Crash!;
        var explanation =
            $"{instance} crashed at {crash.Ts.ToString("u")}, and the run that ended there signed off with "
            + $"{hit.Value.Description}: \"{CrashOutput.Clip(hit.Value.Line)}\". These are the last lines that run "
            + $"printed before it exited:\n{string.Join("\n", excerpt)}";

        return new RootCauseFinding(
            RootCauseSignature.FatalConsoleOutput, "Fatal error in the crashed run's output",
            Confidence.Confirmed, explanation, new[] { crash }, metricFacts, healthChecks, excerpt);
    }

    /// <summary>Best (lowest <see cref="Confidence"/> value = highest trust) first; ties keep the
    /// table's declaration order (a stable sort, since <paramref name="matched"/> was built in that order).</summary>
    private static IReadOnlyList<RootCauseFinding> Rank(List<RootCauseFinding> matched) =>
        matched.OrderBy(f => (int)f.Confidence).ToList();

    /// <summary>
    /// Port conflict / bind failure: a start (or restart) that crashed or failed within
    /// <see cref="StartupWindow"/> without an intervening <c>instance_ready</c>. Always
    /// <see cref="Confidence.Likely"/> — kgsm's event log records THAT the start didn't take, not
    /// WHY, so this never claims to be confirmed.
    /// </summary>
    private static RootCauseFinding? MatchPortConflict(
        string instance, IReadOnlyList<AuditEventRow> ascending,
        IReadOnlyList<MetricFact> metricFacts, IReadOnlyList<HealthCheck> healthChecks)
    {
        AuditEventRow? pendingStart = null;
        var reachedReady = false;
        var pairs = new List<(AuditEventRow Start, AuditEventRow Crash)>();

        foreach (var e in ascending)
        {
            switch (e.Type)
            {
                case "instance_started" or "instance_restarted":
                    pendingStart = e;
                    reachedReady = false;
                    break;
                case "instance_ready":
                    reachedReady = true;
                    break;
                case "instance_crashed" or "instance_failed":
                    if (pendingStart is not null && !reachedReady && (e.Ts - pendingStart.Ts) <= StartupWindow)
                        pairs.Add((pendingStart, e));
                    pendingStart = null;
                    reachedReady = false;
                    break;
                case "instance_stopped":
                    // A deliberate stop resolves the pending start — not a failed-to-bind signal.
                    pendingStart = null;
                    reachedReady = false;
                    break;
            }
        }

        if (pairs.Count == 0)
            return null;

        var evidence = pairs
            .OrderByDescending(p => p.Crash.Ts)
            .Take(MaxEvidenceEventsPerFinding)
            .SelectMany(p => new[] { p.Start, p.Crash })
            .DistinctBy(r => r.Id)
            .ToList();

        var times = pairs.Count == 1 ? "once" : $"{pairs.Count} times";
        var explanation =
            $"{instance} started but crashed or failed to stay up {times} shortly after (within " +
            $"{FormatWindow(StartupWindow)}), never reaching a ready state. That pattern is consistent " +
            "with a port conflict or bind failure, though kgsm's event log doesn't record the exact " +
            "reason — treat this as a likely lead, not a confirmed cause.";

        return new RootCauseFinding(
            RootCauseSignature.PortConflictOrBindFailure, "Port conflict / bind failure",
            Confidence.Likely, explanation, evidence, metricFacts, healthChecks);
    }

    /// <summary>
    /// Update-mid-run crash loop: one or more crashes landing within <see cref="UpdateCrashWindow"/>
    /// after an update-finished/updated/version-updated event. Two or more such crashes read as a
    /// loop (<see cref="Confidence.Confirmed"/> — an unambiguous repeated pattern); a single crash is
    /// <see cref="Confidence.Likely"/> (a well-supported but not yet repeated inference).
    /// </summary>
    private static RootCauseFinding? MatchUpdateCrashLoop(
        string instance, IReadOnlyList<AuditEventRow> ascending,
        IReadOnlyList<MetricFact> metricFacts, IReadOnlyList<HealthCheck> healthChecks)
    {
        var updates = ascending.Where(e => UpdateEventTypes.Contains(e.Type)).ToList();
        if (updates.Count == 0)
            return null;

        var pairs = new List<(AuditEventRow Update, AuditEventRow Crash)>();
        foreach (var crash in ascending.Where(e => e.Type == "instance_crashed"))
        {
            var precedingUpdate = updates
                .Where(u => u.Ts <= crash.Ts && (crash.Ts - u.Ts) <= UpdateCrashWindow)
                .OrderByDescending(u => u.Ts)
                .FirstOrDefault();
            if (precedingUpdate is not null)
                pairs.Add((precedingUpdate, crash));
        }

        if (pairs.Count == 0)
            return null;

        var evidence = pairs
            .OrderByDescending(p => p.Crash.Ts)
            .Take(MaxEvidenceEventsPerFinding)
            .SelectMany(p => new[] { p.Update, p.Crash })
            .DistinctBy(r => r.Id)
            .ToList();

        var isLoop = pairs.Count >= 2;
        var confidence = isLoop ? Confidence.Confirmed : Confidence.Likely;
        var times = pairs.Count == 1 ? "once" : $"{pairs.Count} times";
        var loopWording = isLoop ? "an update-triggered crash loop" : "likely triggered by that update";
        var explanation =
            $"{instance} crashed {times} within {FormatWindow(UpdateCrashWindow)} of an update finishing — {loopWording}.";

        return new RootCauseFinding(
            RootCauseSignature.UpdateMidRunCrashLoop, "Update-mid-run crash loop",
            confidence, explanation, evidence, metricFacts, healthChecks);
    }

    /// <summary>
    /// Disk full: the health snapshot's own "disk" verdict is already <see cref="CheckState.Fail"/>
    /// (critically full — reuses <see cref="HealthCheckAggregator"/>'s judgment rather than a second
    /// threshold) AND a deploy/download/uninstall failure landed in the window. Both facts are
    /// measured, and their coincidence is unambiguous, so this is <see cref="Confidence.Confirmed"/>.
    /// </summary>
    private static RootCauseFinding? MatchDiskFull(
        string instance, IReadOnlyList<AuditEventRow> ascending,
        IReadOnlyList<HealthCheck> healthChecks, IReadOnlyList<MetricFact> metricFacts)
    {
        var diskCheck = healthChecks.FirstOrDefault(c => c.Name == "disk");
        if (diskCheck is not { State: CheckState.Fail })
            return null;

        var failures = ascending.Where(e => DiskFailureAdjacentEventTypes.Contains(e.Type))
            .OrderByDescending(e => e.Ts)
            .Take(MaxEvidenceEventsPerFinding)
            .ToList();
        if (failures.Count == 0)
            return null;

        var explanation =
            $"{instance}'s host disk is critically full ({diskCheck.Detail.TrimEnd('.')}) and " +
            $"{(failures.Count == 1 ? "a" : failures.Count.ToString())} " +
            $"{Plural(failures.Count, "failure", "failures")} landed in the same window — consistent with " +
            "running out of disk space.";

        return new RootCauseFinding(
            RootCauseSignature.DiskFull, "Disk full",
            Confidence.Confirmed, explanation, failures, metricFacts, healthChecks);
    }

    /// <summary>
    /// Split-brain: the most recent run-state event says the instance started/restarted/became
    /// ready, but the live health snapshot reads it as NOT running. A direct contradiction between
    /// two measured, authoritative sources — no inference involved — so <see cref="Confidence.Confirmed"/>.
    /// </summary>
    private static RootCauseFinding? MatchSplitBrain(
        string instance, IReadOnlyList<AuditEventRow> ascending,
        InstanceHealthSnapshot health, IReadOnlyList<HealthCheck> healthChecks, IReadOnlyList<MetricFact> metricFacts)
    {
        if (health.Running)
            return null;

        var lastRunState = ascending.LastOrDefault(e =>
            RunningImpliesEventTypes.Contains(e.Type) || e.Type is "instance_stopped" or "instance_crashed" or "instance_failed");
        if (lastRunState is null || !RunningImpliesEventTypes.Contains(lastRunState.Type))
            return null;

        var explanation =
            $"{instance}'s most recent recorded event ({FriendlyType(lastRunState.Type)} at {lastRunState.Ts:u}) says " +
            "it started, but the live status reads it as NOT running — the event log and the measured live state " +
            "disagree (a split-brain). This pins the symptom, not the underlying mechanism.";

        return new RootCauseFinding(
            RootCauseSignature.SplitBrain, "Split-brain (event log vs. live state)",
            Confidence.Confirmed, explanation, new[] { lastRunState }, metricFacts, healthChecks);
    }

    // --- No signature matched: honest correlation, never a guessed cause ---------------------------

    private static RootCauseFinding BuildCorrelation(
        string instance, IReadOnlyList<AuditEventRow> events,
        IReadOnlyList<MetricFact> metricFacts, IReadOnlyList<HealthCheck> healthChecks,
        CrashConsole console)
    {
        // events is already ts-DESC (most-recent-first). Prefer non-routine activity (skip player
        // join/leave noise); fall back to whatever there is, even routine events, rather than an
        // empty correlation when SOME activity exists.
        var salient = events.Where(e => e.Type is not ("instance_player_joined" or "instance_player_left"))
            .Take(MaxSalientCorrelation)
            .ToList();
        if (salient.Count == 0)
            salient = events.Take(MaxSalientCorrelation).ToList();

        var explanation = salient.Count == 0
            ? $"No recorded activity for {instance} in the requested window — there's nothing to correlate a cause from."
            : $"No known failure signature matched for {instance}. Most notable recent activity: " +
              string.Join(", ", salient.Select(e => $"{FriendlyType(e.Type)} at {e.Ts:u}")) + ".";

        // A crash whose output matched no known signature is still the best evidence in the window —
        // the process's own last words. Attaching them here rather than raising a finding keeps the
        // strength honest: unrecognised output is a lead to read, not a diagnosis, and the reader is
        // better placed to recognise a game's own wording than this table is.
        IReadOnlyList<string>? excerpt = null;
        if (console.HasOutput)
        {
            excerpt = CrashOutput.TailExcerpt(console.Lines);
            explanation +=
                $" Nothing in the crashed run's output matched a known fatal signature either, but these "
                + $"are the last lines it printed before exiting at {console.Crash!.Ts.ToString("u")}:\n"
                + string.Join("\n", excerpt);
        }

        // Said whether or not there was output to quote: how a process exited is measured separately
        // from what it printed, and a run that died without a word is exactly where the code is the
        // only evidence there is.
        if (DescribeExit(console.ExitCode) is { } exitNote)
            explanation += " " + exitNote;
        else if (console.State == FactsState.Unavailable)
        {
            explanation +=
                " The crashed run's own console couldn't be read, so what the server printed on its way "
                + "out is unknown — that's a gap in the evidence, not an absence of errors.";
        }

        return new RootCauseFinding(
            RootCauseSignature.None, "No signature matched — correlation only",
            Confidence.Possible, explanation, salient, metricFacts, healthChecks, excerpt);
    }

    /// <summary>
    /// One sentence about how the crashed run's process exited, or null when the supervisor could not
    /// read a code.
    /// <para>
    /// <b>A signal is the fact worth carrying.</b> A process terminated by a signal did not choose to
    /// exit — that is something the console cannot show, because there is nothing for it to print, and
    /// it is precisely the case where output alone reads as "the server was fine and then stopped".
    /// The wording names what produces the signal and stops there: which of those causes it actually
    /// was is not something an exit code can settle.
    /// </para>
    /// <para>
    /// A plain code says much less, and code 0 says least of all — game servers routinely exit 0 on a
    /// fatal error, so it is reported without letting it read as a clean shutdown.
    /// </para>
    /// </summary>
    private static string? DescribeExit(int? exitCode)
    {
        if (exitCode is not { } code)
            return null;

        // POSIX shells report a signal death as 128+N, and that is what the supervisor reads back.
        if (code is > 128 and < 160 && SignalNames.TryGetValue(code - 128, out string? signal))
            return $"The process was terminated by {signal} (exit {code}) rather than exiting on its own. "
                + "A supervisor's hard teardown, an operator or tool killing it, and the kernel's "
                + "out-of-memory killer all end a process this way, and the exit code cannot tell them "
                + "apart — establishing which one it was needs the host's own logs.";

        return code == 0
            ? "The process exited with code 0. That is not evidence of a clean shutdown — game servers "
                + "routinely exit 0 after a fatal error — so it neither supports nor rules out a crash."
            : $"The process exited with code {code}.";
    }

    /// <summary>
    /// The signals worth naming: the ways a game server actually gets killed. An unlisted signal falls
    /// through to the plain exit-code wording rather than being described with a number a reader would
    /// have to look up anyway.
    /// </summary>
    private static readonly Dictionary<int, string> SignalNames = new()
    {
        [4] = "SIGILL", [6] = "SIGABRT", [7] = "SIGBUS", [8] = "SIGFPE",
        [9] = "SIGKILL", [11] = "SIGSEGV", [15] = "SIGTERM",
    };

    private static RootCauseFinding UnavailableFinding(
        string instance, IReadOnlyList<MetricFact> metricFacts, IReadOnlyList<HealthCheck> healthChecks) =>
        new(
            RootCauseSignature.None, "Event timeline unavailable",
            Confidence.Possible,
            $"Root-cause analysis for {instance} needs the engine event timeline, which is unavailable " +
            "right now — the engine's event journal couldn't be read. That isn't evidence nothing happened; " +
            "the timeline just couldn't be read, so no failure signature could be checked.",
            Array.Empty<AuditEventRow>(), metricFacts, healthChecks);

    // --- Metrics-window evidence (context only — no rule gates on it) ------------------------------

    private static readonly IReadOnlyList<(string Metric, string Label)> ContextMetrics = new[]
    {
        ("cpuPctCore", "CPU"), ("memBytes", "memory"),
    };

    /// <summary>
    /// Reuses <see cref="PerformanceReport.Stats"/>/<see cref="PerformanceReport.FormatBytes"/> (made
    /// internal for exactly this) so the avg/peak wording matches <c>get_performance</c>'s trend
    /// summary rather than drifting. Only produces facts for a <see cref="PerformanceState.Live"/>
    /// window with data — an unavailable or empty window yields no facts, never a fabricated one.
    /// </summary>
    private static IReadOnlyList<MetricFact> BuildMetricFacts(ServerMetricsHistory metrics)
    {
        if (metrics.State != PerformanceState.Live)
            return Array.Empty<MetricFact>();

        var facts = new List<MetricFact>();
        foreach (var (metric, label) in ContextMetrics)
        {
            if (PerformanceReport.Stats(metrics, metric) is not { } stats)
                continue;

            var detail = metric == "memBytes"
                ? $"{label} averaged {PerformanceReport.FormatBytes((long)stats.avg)} " +
                  $"(peak {PerformanceReport.FormatBytes((long)stats.max)}) over the last {metrics.Range}."
                : $"{label} averaged {stats.avg:0.#}% of one core (peak {stats.max:0.#}%) over the last {metrics.Range}.";

            facts.Add(new MetricFact(metric, detail));
        }

        return facts;
    }

    // --- Small formatting helpers --------------------------------------------------------------

    private static string FormatWindow(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{span.TotalMinutes:0} min" : $"{span.TotalHours:0.#}h";

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    /// <summary>Friendly label for a raw kgsm event type, falling back to the raw string for a type
    /// this host has never seen — honest, never a guessed grammar (mirrors <see cref="Audit.AuditReport"/>).</summary>
    private static string FriendlyType(string type) => type switch
    {
        "instance_started" => "started",
        "instance_restarted" => "restarted",
        "instance_stopped" => "stopped",
        "instance_ready" => "became ready",
        "instance_crashed" => "crashed",
        "instance_failed" => "failed (gave up restarting)",
        "instance_updated" => "updated",
        "instance_update_finished" => "update finished",
        "instance_version_updated" => "version updated",
        "instance_installed" => "installed",
        "instance_uninstalled" => "uninstalled",
        "instance_backup_created" => "backed up",
        "instance_deploy_failed" => "deploy failed",
        "instance_download_failed" => "download failed",
        "instance_uninstall_failed" => "uninstall failed",
        _ => type,
    };
}
