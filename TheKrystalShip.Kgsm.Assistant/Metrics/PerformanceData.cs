using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Metrics;

/// <summary>
/// The outcome of a <c>get_performance</c> read — which kind of result the metrics monitor
/// yielded for the requested server. Only <see cref="Live"/> is card-worthy (it carries measured
/// values); the other two are honest "nothing to show" outcomes the dispatcher keeps summary-only.
/// </summary>
public enum PerformanceState
{
    /// <summary>A running server with a live metrics frame — measured CPU/memory/etc. to report.</summary>
    Live,

    /// <summary>
    /// The server exists but is not in the metrics frame (the monitor lists running servers only),
    /// so it has no live resource usage. NOT an error — a stopped server simply isn't measured.
    /// </summary>
    NotRunning,

    /// <summary>
    /// The metrics monitor could not be reached or its response could not be read/parsed — an honest
    /// "couldn't read", NOT evidence the server is idle. All values are absent (the measured-or-unknown rule).
    /// </summary>
    MonitorUnavailable,
}

/// <summary>
/// The <c>get_performance</c> tool's structured card payload (the surface half of its
/// <see cref="ToolResult{TData}"/>): one server's LIVE resource usage as a snapshot of current
/// measured values — CPU (as a percentage of one core), memory, network throughput, block-I/O
/// throughput, on-disk footprint, and process count. Built only for a <see cref="PerformanceState.Live"/>
/// read (a running server with a live frame); a not-running / monitor-unavailable outcome is
/// summary-only, so there is no card to render.
/// <para>
/// Every measured field is nullable and follows the monitor's honest wire contract: <c>null</c> means
/// "not measured" (the meter isn't accounting that axis for this server), never a fabricated 0. A
/// surface renders <c>null</c> as "not measured", never as zero usage.
/// </para>
/// </summary>
/// <param name="Instance">The resolved instance name the metrics are for (also the card subject id).</param>
/// <param name="State">Which outcome produced this payload (only <see cref="PerformanceState.Live"/> carries values).</param>
/// <param name="CpuPctCore">CPU usage as a percentage of ONE core (a multi-core server can exceed 100); <c>null</c> when not measured.</param>
/// <param name="MemBytes">Charged memory in bytes; <c>null</c> when not measured.</param>
/// <param name="RxBps">Network receive throughput in bytes/sec; <c>null</c> when the meter isn't measuring this server.</param>
/// <param name="TxBps">Network transmit throughput in bytes/sec; <c>null</c> when not measured.</param>
/// <param name="IoReadBps">Block-I/O read throughput in bytes/sec; <c>null</c> when I/O accounting is absent.</param>
/// <param name="IoWriteBps">Block-I/O write throughput in bytes/sec; <c>null</c> when I/O accounting is absent.</param>
/// <param name="DiskBytes">On-disk footprint of the instance's working directory in bytes; <c>null</c> when not yet walked / unreadable.</param>
/// <param name="Pids">Live process/thread count; <c>null</c> when not measured.</param>
/// <param name="Range">The history window (<c>1h</c>/<c>24h</c>/<c>7d</c>/<c>30d</c>) when this is a
/// TREND read; <c>null</c> for a point-in-time snapshot. Its presence is what tells a surface to render
/// a chart from <see cref="Series"/> instead of the snapshot value rows.</param>
/// <param name="Series">The per-metric time series over <see cref="Range"/>, keyed by the monitor's
/// metric names (<c>cpuPctCore</c>, <c>memBytes</c>, …). <c>null</c> for a snapshot; present (possibly
/// empty) for a trend read.</param>
public sealed record PerformanceData(
    string Instance,
    PerformanceState State,
    double? CpuPctCore,
    long? MemBytes,
    long? RxBps,
    long? TxBps,
    long? IoReadBps,
    long? IoWriteBps,
    long? DiskBytes,
    int? Pids,
    string? Range = null,
    IReadOnlyDictionary<string, IReadOnlyList<PerformanceSeriesPoint>>? Series = null);

/// <summary>One plotted point on a <c>get_performance</c> trend series: a timestamp and its value
/// (the bucket average at the rollup tier). The surface renders these as a chart line.</summary>
public sealed record PerformanceSeriesPoint(DateTimeOffset Ts, double Value);
