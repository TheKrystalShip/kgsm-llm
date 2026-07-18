using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;

namespace TheKrystalShip.Kgsm.Assistant.RootCause;

/// <summary>
/// Which known KGSM failure pattern <see cref="RootCauseAggregator"/> matched, drawn from the
/// locked rules table (toolbox-plan §3.4/§7·Q1). <see cref="None"/> means no rule matched — the
/// aggregator falls back to an honest correlation of the most salient recent events, never a
/// guessed cause (§3.0).
/// </summary>
public enum RootCauseSignature
{
    /// <summary>No rule matched. The finding carrying this is a correlation, not a diagnosis.</summary>
    None,

    /// <summary>A start (or restart) crashed or failed shortly after, without ever reaching
    /// "ready" — consistent with a port conflict or bind failure. kgsm's event log carries no
    /// dedicated bind-failure reason, so this is always reported at <see cref="Confidence.Likely"/>,
    /// never <see cref="Confidence.Confirmed"/>.</summary>
    PortConflictOrBindFailure,

    /// <summary>One or more crashes landed shortly after an update finished — the update is the
    /// likely trigger. Two or more such crashes read as a crash loop
    /// (<see cref="Confidence.Confirmed"/>); a single one is <see cref="Confidence.Likely"/>.</summary>
    UpdateMidRunCrashLoop,

    /// <summary>Host disk headroom is critically low (the health check's own "disk" verdict is
    /// <see cref="CheckState.Fail"/>) AND a deploy/download/uninstall failure landed in the same
    /// window — the failure is consistent with running out of disk space.</summary>
    DiskFull,

    /// <summary>The most recent run-state event says the instance started/restarted/became ready,
    /// but the live health snapshot reads it as not running — the event log and the measured live
    /// state disagree. A direct, deterministic contradiction between two authoritative sources, so
    /// this is reported at <see cref="Confidence.Confirmed"/> even though it explains a symptom
    /// (state disagreement) rather than a root mechanism.</summary>
    SplitBrain,
}

/// <summary>
/// One fact drawn from the metrics window — supporting context for a <see cref="RootCauseFinding"/>,
/// never a rule trigger by itself (none of the locked rules gate on per-instance resource usage;
/// only host disk headroom, which comes from the health snapshot). <see cref="Metric"/> is the
/// monitor's metric key (<c>cpuPctCore</c>, <c>memBytes</c>); <see cref="Detail"/> is the
/// already-formatted human sentence (avg/peak over the window).
/// </summary>
public sealed record MetricFact(string Metric, string Detail);

/// <summary>
/// One ranked entry in a <see cref="RootCauseData"/> result: either a matched failure signature
/// with its evidence chain, or (when <see cref="Signature"/> is <see cref="RootCauseSignature.None"/>)
/// an honest correlation of recent activity. <see cref="Events"/>/<see cref="Metrics"/>/
/// <see cref="HealthChecks"/> are the concrete evidence the deterministic rule read — the model
/// narrates <see cref="Explanation"/>, it never invents it (§3.0).
/// </summary>
/// <param name="Signature">The matched pattern, or <see cref="RootCauseSignature.None"/> for a correlation.</param>
/// <param name="Label">A short display name for the finding (e.g. "Update-mid-run crash loop").</param>
/// <param name="Confidence">Trust in this finding's conclusion — never <see cref="Confidence.Confirmed"/>
/// for an inferred cause; only for a directly-measured contradiction (split-brain) or an unambiguous
/// deterministic read (a repeated pattern, a disk-full + failure coincidence).</param>
/// <param name="Explanation">The deterministic, model-facing sentence(s) explaining the finding —
/// authored here, never by the model.</param>
/// <param name="Events">The specific engine events that produced this finding (most-recent-first).</param>
/// <param name="Metrics">Metrics-window facts attached as supporting context.</param>
/// <param name="HealthChecks">Health-snapshot checks attached as supporting context (liveness/logs/updates/disk).</param>
public sealed record RootCauseFinding(
    RootCauseSignature Signature,
    string Label,
    Confidence Confidence,
    string Explanation,
    IReadOnlyList<AuditEventRow> Events,
    IReadOnlyList<MetricFact> Metrics,
    IReadOnlyList<HealthCheck> HealthChecks);

/// <summary>
/// The <c>trace_root_cause</c> tool's structured card payload (toolbox-plan §3.4's capstone
/// aggregator): a ranked list of <see cref="RootCauseFinding"/>s composed deterministically from
/// three sources — the engine event timeline, a metrics window, and a health/status snapshot — for
/// one instance. <see cref="Findings"/>[0] is the best (highest-confidence) finding; when nothing
/// matched, it is always exactly one <see cref="RootCauseSignature.None"/> correlation entry, never
/// an empty list (there is always something to say, even if only "unavailable" or "nothing notable").
/// <para>
/// Each source degrades independently and honestly: <see cref="EventState"/> /
/// <see cref="MetricsState"/> / <see cref="HealthAvailable"/> report whether each one actually
/// answered. A source being unavailable narrows which rules could be evaluated — it never causes a
/// rule to fabricate a match from an absent input.
/// </para>
/// </summary>
/// <param name="Instance">The resolved instance name this trace is about.</param>
/// <param name="Range">The normalized window/range that was queried (e.g. <c>24h</c>).</param>
/// <param name="EventState">Whether the engine event timeline could be read.</param>
/// <param name="MetricsState">Whether the metrics window could be read.</param>
/// <param name="HealthAvailable">Whether the health/status snapshot could be read.</param>
/// <param name="HealthUnavailableReason">Why the health snapshot is absent; non-null only when
/// <paramref name="HealthAvailable"/> is <see langword="false"/>.</param>
/// <param name="Findings">Ranked findings, best (highest-confidence) first — never empty.</param>
/// <param name="EventsConsidered">How many events in the window fed the rules (for transparency).</param>
public sealed record RootCauseData(
    string Instance,
    string Range,
    AuditReadState EventState,
    PerformanceState MetricsState,
    bool HealthAvailable,
    string? HealthUnavailableReason,
    IReadOnlyList<RootCauseFinding> Findings,
    int EventsConsidered);
