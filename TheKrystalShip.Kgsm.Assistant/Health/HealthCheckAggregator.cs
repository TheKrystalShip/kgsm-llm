using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Health;

/// <summary>
/// The deterministic <c>run_health_check</c> synthesis (toolbox-plan §3.4): turns a
/// neutral <see cref="InstanceHealthSnapshot"/> into a ranked <see cref="HealthData"/>
/// card plus a model-grounding <see cref="ToolResult{TData}.Summary"/>. Pure and
/// I/O-free — every fetch happens in the surface's port impl, so this is unit-testable
/// without mocks and is the single home for "what counts as healthy" (no surface
/// duplicates the judgment).
/// <para>
/// Key rule (§D5): KGSM is stateless — there is no desired-state — so a deliberately
/// <em>stopped</em> server is reported as info, never as a failure. The log scan only
/// runs when the instance is running (otherwise the logs are stale). An unknown update
/// status or an unreadable host disk <see cref="CheckState.Skip"/>s — it is never
/// fabricated into a pass.
/// </para>
/// </summary>
public static class HealthCheckAggregator
{
    // Disk-headroom thresholds (% used).
    private const int DiskWarnPercent = 85;
    private const int DiskFailPercent = 95;

    private const int MaxSampleErrorLength = 100;

    /// <summary>
    /// Runs the deterministic sweep. Always returns a result — partial inputs degrade to
    /// <see cref="CheckState.Skip"/> checks, never errors.
    /// </summary>
    /// <param name="snapshot">The fetched, neutral inputs for one instance.</param>
    /// <param name="instanceId">The resolved instance name (the result's subject).</param>
    public static ToolResult<HealthData> Run(InstanceHealthSnapshot snapshot, string instanceId)
    {
        var liveness = CheckLiveness(snapshot);
        var logs = CheckLogs(snapshot);
        var updates = CheckUpdates(snapshot);
        var disk = CheckDisk(snapshot);

        var checks = new[] { liveness, logs, updates, disk };

        var overall = WorstNonSkip(checks);
        var passed = checks.Count(c => c.State == CheckState.Pass);
        var skipped = checks.Count(c => c.State == CheckState.Skip);

        var data = new HealthData(overall, checks, passed, checks.Length, skipped);
        var summary = BuildSummary(instanceId, snapshot.Running, overall, logs, updates, disk);

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

    private static HealthCheck CheckLogs(InstanceHealthSnapshot s)
    {
        // Logs are only meaningful for a running server; a stopped one's logs are stale.
        if (!s.Running)
            return new HealthCheck(
                "logs", CheckState.Skip, Severity.Info, "Log scan skipped — instance not running.");

        var (count, sample) = TallyErrors(s.RecentLogLines);
        if (count == 0)
            return new HealthCheck("logs", CheckState.Pass, Severity.Success, "No errors in recent logs.");

        var detail = $"{count} error line{(count == 1 ? "" : "s")} in recent logs"
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
    /// Authors the deterministic grounding summary (§3.6): a headline conveying the
    /// overall verdict + liveness, then the other checks' one-liners. The model
    /// paraphrases this; it never invents facts.
    /// </summary>
    private static string BuildSummary(
        string id, bool running, CheckState overall,
        HealthCheck logs, HealthCheck updates, HealthCheck disk)
    {
        var headline = (overall, running) switch
        {
            (CheckState.Fail, _) => $"{id}: health check found problems.",
            (CheckState.Warn, _) => $"{id}: passed with warnings.",
            (_, true) => $"{id}: healthy.",
            (_, false) => $"{id}: stopped (idle).",
        };

        var rest = string.Join(" ", new[] { logs.Detail, updates.Detail, disk.Detail }
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

    // Coarse V1 severity scan (toolbox-plan: format-aware tally is a later upgrade).
    private static bool LooksLikeError(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
        || line.Contains("SEVERE", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
