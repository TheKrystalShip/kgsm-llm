using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Metrics;

/// <summary>
/// The deterministic <c>get_performance</c> composer (mirrors <see cref="Health.HealthCheckAggregator"/>):
/// turns a neutral <see cref="ServerMetricsReading"/> into a <see cref="PerformanceData"/> card plus a
/// model-grounding <see cref="ToolResult{TData}.Summary"/>. Pure and I/O-free — the socket fetch happens
/// in the port impl — so it is unit-testable without mocks and is the single home for how a live snapshot
/// is worded.
/// <para>
/// Honesty rules baked in: a <see cref="PerformanceState.NotRunning"/> server has no live metrics (never
/// narrated as "0 usage"); a <see cref="PerformanceState.MonitorUnavailable"/> read is an honest "couldn't
/// read", explicitly NOT a sign the server is idle; and any measured axis whose value is <c>null</c> ("not
/// measured") is omitted from the grounding sentence rather than shown as 0 (the ecosystem never-fabricate rule).
/// </para>
/// </summary>
public static class PerformanceReport
{
    /// <summary>
    /// Builds the result envelope for one server's live-metrics read. Always returns a result: a
    /// not-running / monitor-unavailable reading degrades to an honest summary with a null-valued
    /// <see cref="PerformanceData"/>, never an error.
    /// </summary>
    /// <param name="reading">The neutral, measured-or-absent reading from the metrics port.</param>
    /// <param name="instance">The resolved instance name (the result's subject).</param>
    public static ToolResult<PerformanceData> Build(ServerMetricsReading reading, string instance)
    {
        var data = new PerformanceData(
            instance, reading.State,
            reading.CpuPctCore, reading.MemBytes, reading.RxBps, reading.TxBps,
            reading.IoReadBps, reading.IoWriteBps, reading.DiskBytes, reading.Pids);

        var (confidence, summary) = reading.State switch
        {
            // A live frame is a deterministic read of measured facts.
            PerformanceState.Live => (Confidence.Confirmed, BuildLiveSummary(instance, reading)),
            PerformanceState.NotRunning => (Confidence.Confirmed,
                $"{instance} isn't running, so there are no live resource metrics to report."),
            // "Couldn't read" — not measured, so only a possible conclusion, and NOT a claim of idleness.
            _ => (Confidence.Possible,
                $"Live resource metrics for {instance} are unavailable right now — the metrics monitor isn't " +
                "reachable. That isn't a sign the server is idle; the numbers just couldn't be read."),
        };

        return new ToolResult<PerformanceData>(
            Tool: LlmTools.GetPerformance,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Metrics, instance),
            Summary: summary,
            Data: data);
    }

    /// <summary>
    /// Builds the result envelope for a windowed TREND read (the model asked "how has X been doing over
    /// the last …?"). Carries the per-metric series so a surface can draw a chart, and a grounding summary
    /// that states the numbers the model can't see in the chart — the CPU and memory avg/peak over the
    /// window. An unreachable monitor is an honest "couldn't read history" (no series); an empty series is
    /// an honest "no history recorded in that window" — neither is narrated as idleness.
    /// </summary>
    /// <param name="history">The neutral windowed history from the metrics port.</param>
    /// <param name="instance">The resolved instance name (the result's subject).</param>
    public static ToolResult<PerformanceData> BuildHistory(ServerMetricsHistory history, string instance)
    {
        var series = history.Series.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<PerformanceSeriesPoint>)kv.Value.Select(p => new PerformanceSeriesPoint(p.Ts, p.Value)).ToList());

        var data = new PerformanceData(
            instance, history.State,
            CpuPctCore: null, MemBytes: null, RxBps: null, TxBps: null,
            IoReadBps: null, IoWriteBps: null, DiskBytes: null, Pids: null,
            Range: history.Range, Series: series);

        bool hasData = series.Values.Any(pts => pts.Count > 0);

        var (confidence, summary) = history.State switch
        {
            PerformanceState.Live when hasData => (Confidence.Confirmed, BuildTrendSummary(instance, history)),
            PerformanceState.Live => (Confidence.Confirmed,
                $"No resource history is recorded for {instance} over the last {history.Range} yet — the " +
                "trend is empty (the server may have only just started being measured). That isn't a sign it's idle."),
            _ => (Confidence.Possible,
                $"The resource history for {instance} is unavailable right now — the metrics monitor isn't " +
                "reachable. That isn't a sign the server is idle; the trend just couldn't be read."),
        };

        return new ToolResult<PerformanceData>(
            Tool: LlmTools.GetPerformance,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Metrics, instance),
            Summary: summary,
            Data: data);
    }

    /// <summary>
    /// Authors the grounding sentence for a trend read: the CPU (% of one core) and memory avg/peak over
    /// the window, drawn from the series the model can't visually read. Each clause is dropped when its
    /// metric isn't in the series (never a fabricated 0).
    /// </summary>
    private static string BuildTrendSummary(string instance, ServerMetricsHistory h)
    {
        var parts = new List<string> { $"Over the last {h.Range}, {instance}:" };

        if (Stats(h, "cpuPctCore") is { } cpu)
            parts.Add($"CPU averaged {cpu.avg:0.#}% of one core (peak {cpu.max:0.#}%).");

        if (Stats(h, "memBytes") is { } mem)
            parts.Add($"memory averaged {FormatBytes((long)mem.avg)} (peak {FormatBytes((long)mem.max)}).");

        if (parts.Count == 1)
            parts.Add("a trend was recorded (see the chart).");

        return string.Join(" ", parts);
    }

    /// <summary>avg/min/max over a metric's series, or null when that metric isn't present. Internal
    /// (not private) so <see cref="RootCause.RootCauseAggregator"/> can reuse the same computation for
    /// its metrics-window evidence facts rather than duplicating it.</summary>
    internal static (double avg, double min, double max)? Stats(ServerMetricsHistory h, string metric)
    {
        if (!h.Series.TryGetValue(metric, out var points) || points.Count == 0)
            return null;
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var p in points)
        {
            sum += p.Value;
            if (p.Value < min) min = p.Value;
            if (p.Value > max) max = p.Value;
        }
        return (sum / points.Count, min, max);
    }

    /// <summary>
    /// Authors the grounding sentence for a live frame: CPU + memory always lead (a live frame carries
    /// both), then a network clause and a disk-I/O clause, each dropped when its source is null. Only when
    /// network is entirely unmeasured is that called out explicitly (so the model doesn't imply "no traffic"
    /// from a missing meter); process count closes when present.
    /// </summary>
    private static string BuildLiveSummary(string instance, ServerMetricsReading r)
    {
        var parts = new List<string>
        {
            $"{instance} is using {FormatCpu(r.CpuPctCore)} of one CPU core and {FormatBytes(r.MemBytes)} of memory.",
        };

        // Network: both directions measured together (same eBPF meter); drop the clause if either is null.
        if (r.RxBps is not null && r.TxBps is not null)
            parts.Add($"Receiving {FormatRate(r.RxBps)}, sending {FormatRate(r.TxBps)}.");

        // Disk I/O: report whatever half is measured; drop the clause when neither is.
        if (r.IoReadBps is not null || r.IoWriteBps is not null)
            parts.Add($"Disk I/O {FormatRate(r.IoReadBps)} read, {FormatRate(r.IoWriteBps)} write.");

        if (r.DiskBytes is not null)
            parts.Add($"On-disk footprint {FormatBytes(r.DiskBytes)}.");

        if (r.Pids is not null)
            parts.Add($"{r.Pids} process{(r.Pids == 1 ? "" : "es")}.");

        // Honest note only when network throughput isn't being measured at all for this server.
        if (r.RxBps is null && r.TxBps is null)
            parts.Add("Network throughput isn't being measured for this server.");

        return string.Join(" ", parts);
    }

    // --- Formatting helpers (measured-or-"not measured", never a fabricated 0) -----------------------

    private static string FormatCpu(double? pct) =>
        pct is null ? "an unmeasured amount" : $"{pct.Value:0.#}%";

    /// <summary>A byte size as B / KB / MB / GB (1024-based); a null (unmeasured) source renders "not
    /// measured". Internal (not private) so <see cref="RootCause.RootCauseAggregator"/> can reuse the
    /// same formatting for its metrics-window evidence facts.</summary>
    internal static string FormatBytes(long? bytes)
    {
        if (bytes is null)
            return "not measured";

        double b = bytes.Value;
        return b >= 1024L * 1024 * 1024 ? $"{b / (1024.0 * 1024 * 1024):0.#} GB"
            : b >= 1024 * 1024 ? $"{b / (1024.0 * 1024):0.#} MB"
            : b >= 1024 ? $"{b / 1024.0:0.#} KB"
            : $"{bytes.Value} B";
    }

    /// <summary>A byte-rate as B/s … GB/s; a null (unmeasured) source renders "not measured".</summary>
    private static string FormatRate(long? bytesPerSecond) =>
        bytesPerSecond is null ? "not measured" : $"{FormatBytes(bytesPerSecond)}/s";
}
