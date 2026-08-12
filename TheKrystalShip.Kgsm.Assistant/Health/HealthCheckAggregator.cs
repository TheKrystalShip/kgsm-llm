using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Health;

/// <summary>
/// The deterministic <c>run_health_check</c> synthesis: turns a
/// neutral <see cref="InstanceHealthSnapshot"/> into a ranked <see cref="HealthData"/>
/// card plus a model-grounding <see cref="ToolResult{TData}.Summary"/>. Pure and
/// I/O-free — every fetch happens in the surface's port impl, so this is unit-testable
/// without mocks and is the single home for "what counts as healthy" (no surface
/// duplicates the judgment).
/// <para>
/// Key rule: KGSM is stateless — there is no desired-state — so a deliberately
/// <em>stopped</em> server is reported as info, never as a failure. The log scan only
/// runs when the instance is running (otherwise the logs are stale). An unknown update
/// status or an unreadable host disk <see cref="CheckState.Skip"/>s — it is never
/// fabricated into a pass.
/// </para>
/// <para>
/// That rule extends to the SIZE of a sample, not just its absence: a verdict needs enough evidence
/// to be worth stating, so the log scan skips when the surface sampled fewer than
/// <see cref="MinLogLinesForVerdict"/> lines. A clean read of a keyhole is not a clean run, and the
/// pass it would otherwise produce reads to a person as "we looked and it was fine".
/// </para>
/// </summary>
public static class HealthCheckAggregator
{
    // Disk-headroom thresholds (% used).
    private const int DiskWarnPercent = 85;
    private const int DiskFailPercent = 95;

    private const int MaxSampleErrorLength = 100;

    /// <summary>
    /// The smallest log sample a "no errors" verdict may rest on, counted in lines REQUESTED. Below
    /// this the logs check skips: a clean read of a few trailing lines is not evidence of a clean
    /// run, and reporting it as a pass is the difference between "we found nothing" and "there is
    /// nothing to find".
    /// </summary>
    private const int MinLogLinesForVerdict = 25;

    /// <summary>
    /// How recently a crash-restart still belongs in the answer to "is it healthy". Inside this
    /// window the crash is the most useful thing anyone can be told about the server; past it, one
    /// that has stayed up is demonstrating the stability the supervisor credited it with long before.
    /// <para>
    /// Deliberately not the supervisor's own <c>RestartStabilitySeconds</c> (300s), which answers a
    /// different question — whether to reset a failure streak, a supervision decision — and is far too
    /// short to govern what a person is told.
    /// </para>
    /// </summary>
    private static readonly TimeSpan RecentRestartWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Runs the deterministic sweep. Always returns a result — partial inputs degrade to
    /// <see cref="CheckState.Skip"/> checks, never errors.
    /// </summary>
    /// <param name="snapshot">The fetched, neutral inputs for one instance.</param>
    /// <param name="instanceId">The resolved instance name (the result's subject).</param>
    /// <param name="now">The current time, injected so the stability check is testable.</param>
    public static ToolResult<HealthData> Run(
        InstanceHealthSnapshot snapshot, string instanceId, DateTimeOffset? now = null)
    {
        var liveness = CheckLiveness(snapshot);
        var stability = CheckStability(snapshot, now ?? DateTimeOffset.UtcNow);
        var logs = CheckLogs(snapshot);
        var updates = CheckUpdates(snapshot);
        var disk = CheckDisk(snapshot);
        var ports = CheckPorts(snapshot);

        var checks = new[] { liveness, stability, logs, updates, disk, ports };

        var overall = WorstNonSkip(checks);
        var passed = checks.Count(c => c.State == CheckState.Pass);
        var skipped = checks.Count(c => c.State == CheckState.Skip);

        var data = new HealthData(overall, checks, passed, checks.Length, skipped);
        var summary = BuildSummary(instanceId, snapshot.Running, overall, stability, logs, updates, disk, ports);

        return new ToolResult<HealthData>(
            Tool: LlmTools.RunHealthCheck,
            Confidence: Confidence.Confirmed, // deterministic read of measured facts
            Subject: new ResultRef(ResourceKind.Server, instanceId),
            Summary: summary,
            Data: data);
    }

    // --- Individual checks -------------------------------------------------

    private static HealthCheck CheckLiveness(InstanceHealthSnapshot s) =>
        s.Running
            // Stateless KGSM: stopped is NOT a failure — it may be intentional.
            ? new HealthCheck("liveness", CheckState.Pass, Severity.Success, "Running.")
            : new HealthCheck("liveness", CheckState.Pass, Severity.Info, "Stopped (idle).");

    /// <summary>
    /// Whether this run has been up long enough to stand on its own, and what ended the run before it.
    /// <para>
    /// <b>This is the check that stops a crash-restart reading as health.</b> Every other check looks
    /// at the current run: the log sample starts where the run started, so a server that aborted and
    /// came back is examined entirely after the fact and passes cleanly. The crash is not in any of
    /// the evidence, so without this it is in none of the answer either.
    /// </para>
    /// <para>
    /// A restart is only a warning when the supervisor says the previous run <em>failed</em>. A
    /// deliberate stop and restart is somebody doing their job, and an ending nobody recorded is
    /// unknown — reported as such, and never warned on, because not knowing how a run ended is not
    /// evidence that it ended badly.
    /// </para>
    /// </summary>
    private static HealthCheck CheckStability(InstanceHealthSnapshot s, DateTimeOffset now)
    {
        if (!s.Running)
            return new HealthCheck(
                "stability", CheckState.Skip, Severity.Info, "Stability not assessed — instance not running.");

        if (s.Restart is not { } restart)
            return new HealthCheck(
                "stability", CheckState.Skip, Severity.Info,
                "No run history available, so how long this run has been up is unknown.");

        string uptime = Duration(now - restart.At);

        if (now - restart.At > RecentRestartWindow)
            return new HealthCheck(
                "stability", CheckState.Pass, Severity.Success, $"Up {uptime} since the last restart.");

        string exit = restart.PreviousExitCode is { } code ? $", exit {code}" : "";

        return restart.PreviousOutcome switch
        {
            ConsoleRunInfo.CrashedOutcome or ConsoleRunInfo.GaveUpOutcome => new HealthCheck(
                "stability", CheckState.Warn, Severity.Warn,
                $"Restarted {uptime} ago after a crash{exit} — this run's logs begin after it, so a "
                + "clean scan here says nothing about what went wrong."),

            "stopped" or "exited" => new HealthCheck(
                "stability", CheckState.Pass, Severity.Info,
                $"Up {uptime}, after a deliberate stop rather than a failure."),

            _ => new HealthCheck(
                "stability", CheckState.Pass, Severity.Info,
                $"Up {uptime}. How the previous run ended was never recorded, which is not the same as "
                + "it having ended cleanly."),
        };
    }

    /// <summary>
    /// A duration in the coarsest unit that still says something useful — the reader needs to know
    /// whether a restart was minutes or hours ago, not a figure precise enough to be quoted as one.
    /// </summary>
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalMinutes < 1 ? "less than a minute"
            : span.TotalHours < 1 ? Plural((int)span.TotalMinutes, "minute")
            : span.TotalDays < 1 ? Plural((int)span.TotalHours, "hour")
            : Plural((int)span.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    private static HealthCheck CheckLogs(InstanceHealthSnapshot s)
    {
        // Logs are only meaningful for a running server; a stopped one's logs are stale.
        if (!s.Running)
            return new HealthCheck(
                "logs", CheckState.Skip, Severity.Info, "Log scan skipped — instance not running.");

        // Honesty: a probe that only ASKED for a handful of lines cannot support "no errors",
        // however clean it reads — it is a keyhole, not a scan, and a server that has been up for
        // hours prints many screens of routine output after whatever went wrong. Judged on the
        // request rather than the yield: a large request answered with few lines IS the whole log,
        // and reading that clean is real evidence.
        if (s.RecentLogLinesRequested < MinLogLinesForVerdict)
            return new HealthCheck(
                "logs", CheckState.Skip, Severity.Info,
                $"Log scan skipped — only {s.RecentLogLinesRequested} line(s) of output were sampled, "
                + "too few to judge.");

        // Honesty: with NO recent log lines there is nothing to scan, so we cannot claim
        // "no errors" — that would assert a clean bill from zero evidence. Report an honest
        // skip, never a fabricated pass. (Empty here means kgsm surfaced no recent_logs for
        // a running instance — e.g. an unreadable or just-rotated-away log file.)
        if (s.RecentLogLines.Count == 0)
            return new HealthCheck(
                "logs", CheckState.Skip, Severity.Info, "No recent log output to scan.");

        // Scoped to THIS RUN, always — the sample is the current run's console, which begins at the
        // last start. "No errors in recent logs" reads as a statement about the server; it is only
        // ever a statement about the stretch since it last started, and after a restart those are
        // very different claims. The stability check says whether that distinction matters here.
        var (count, sample) = TallyErrors(s.RecentLogLines);
        if (count == 0)
            return new HealthCheck(
                "logs", CheckState.Pass, Severity.Success, "No errors since the server last started.");

        var detail = $"{count} error line{(count == 1 ? "" : "s")} since the server last started"
                     + (sample is null ? "." : $": \"{Truncate(sample, MaxSampleErrorLength)}\".");
        return new HealthCheck("logs", CheckState.Warn, Severity.Warn, detail);
    }

    private static HealthCheck CheckUpdates(InstanceHealthSnapshot s)
    {
        // Honest unknown: KGSM did not check (e.g. a fast read) → skip, never guess.
        if (s.UpdatesAvailable is null)
            return new HealthCheck(
                "updates", CheckState.Skip, Severity.Info, "Update status not checked.");

        if (s.UpdatesAvailable == true)
        {
            var versions = s.CurrentVersion is not null && s.LatestVersion is not null
                ? $" ({s.CurrentVersion} → {s.LatestVersion})"
                : "";
            return new HealthCheck("updates", CheckState.Warn, Severity.Update, $"Update available{versions}.");
        }

        var current = s.CurrentVersion is not null ? $" ({s.CurrentVersion})" : "";
        return new HealthCheck("updates", CheckState.Pass, Severity.Success, $"Up to date{current}.");
    }

    private static HealthCheck CheckDisk(InstanceHealthSnapshot s)
    {
        if (s.HostDisk is null)
            return new HealthCheck(
                "disk", CheckState.Skip, Severity.Info,
                s.HostDiskUnavailableReason is { Length: > 0 } reason
                    ? $"Disk usage unavailable: {reason}."
                    : "Disk usage unavailable.");

        var pct = s.HostDisk.UsedPercent;
        if (pct is null)
            return new HealthCheck(
                "disk", CheckState.Skip, Severity.Info, "Disk usage could not be read.");

        var free = s.HostDisk.Available is { Length: > 0 } a ? $", {a} free" : "";
        if (pct >= DiskFailPercent)
            return new HealthCheck("disk", CheckState.Fail, Severity.Danger,
                $"Disk critically full ({pct}% used{free}).");
        if (pct >= DiskWarnPercent)
            return new HealthCheck("disk", CheckState.Warn, Severity.Warn,
                $"Disk getting full ({pct}% used{free}).");
        return new HealthCheck("disk", CheckState.Pass, Severity.Success,
            $"Disk OK ({pct}% used{free}).");
    }

    private static HealthCheck CheckPorts(InstanceHealthSnapshot s)
    {
        // Port binding is only meaningful for a running server; a stopped one binds nothing.
        if (!s.Running)
            return new HealthCheck(
                "ports", CheckState.Skip, Severity.Info, "Port reachability skipped — instance not running.");

        // Honest unknown: not probed / no ports configured / probe failed → skip, never a fabricated pass.
        if (s.PortsReachable is null)
            return new HealthCheck(
                "ports", CheckState.Skip, Severity.Info,
                s.PortsDetail is { Length: > 0 } reason
                    ? $"Port reachability not checked ({reason})."
                    : "Port reachability not checked.");

        if (s.PortsReachable == true)
            return new HealthCheck(
                "ports", CheckState.Pass, Severity.Success, "Configured ports are active.");

        // Running but ports not bound — a warning, not a hard fail (the server may still be starting up).
        var detail = s.PortsDetail is { Length: > 0 } d
            ? $"Configured ports are not active ({d})."
            : "Configured ports are not active while the server is running.";
        return new HealthCheck("ports", CheckState.Warn, Severity.Warn, detail);
    }

    // --- Synthesis helpers -------------------------------------------------

    /// <summary>The worst state across checks, ignoring <see cref="CheckState.Skip"/>.</summary>
    private static CheckState WorstNonSkip(IReadOnlyList<HealthCheck> checks)
    {
        var worst = CheckState.Pass;
        foreach (var c in checks)
        {
            if (c.State == CheckState.Skip)
                continue;
            if (Rank(c.State) > Rank(worst))
                worst = c.State;
        }
        return worst;
    }

    private static int Rank(CheckState s) => s switch
    {
        CheckState.Fail => 3,
        CheckState.Warn => 2,
        CheckState.Pass => 1,
        _ => 0, // Skip
    };

    /// <summary>
    /// Authors the deterministic grounding summary: a headline conveying the
    /// overall verdict + liveness, then the other checks' one-liners. The model
    /// paraphrases this; it never invents facts.
    /// </summary>
    private static string BuildSummary(
        string id, bool running, CheckState overall,
        HealthCheck stability, HealthCheck logs, HealthCheck updates, HealthCheck disk, HealthCheck ports)
    {
        var headline = (overall, running) switch
        {
            (CheckState.Fail, _) => $"{id}: health check found problems.",
            (CheckState.Warn, _) => $"{id}: passed with warnings.",
            (_, true) => $"{id}: healthy.",
            (_, false) => $"{id}: stopped (idle).",
        };

        // Stability leads the details: when it warns, it is the reason the headline is not "healthy",
        // and it reframes every check after it as being about this run rather than about the server.
        var rest = string.Join(" ", new[] { stability.Detail, logs.Detail, updates.Detail, disk.Detail, ports.Detail }
            .Where(d => !string.IsNullOrWhiteSpace(d)));

        return string.IsNullOrEmpty(rest) ? headline : $"{headline} {rest}";
    }

    private static (int count, string? firstSample) TallyErrors(IReadOnlyList<string> lines)
    {
        var count = 0;
        string? first = null;
        foreach (var line in lines)
        {
            if (!LooksLikeError(line))
                continue;
            count++;
            first ??= line.Trim();
        }
        return (count, first);
    }

    // Coarse V1 severity scan (a format-aware tally is a later upgrade).
    private static bool LooksLikeError(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
        || line.Contains("SEVERE", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
