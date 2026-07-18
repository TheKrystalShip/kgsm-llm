using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Metrics;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One neutral reading of a server's live resource usage, as the <see cref="IServerMetrics"/> port
/// returns it: the raw measured-or-absent inputs the <see cref="PerformanceReport"/> aggregator turns
/// into a card. <see cref="State"/> carries the outcome — <see cref="PerformanceState.Live"/> with
/// values, <see cref="PerformanceState.NotRunning"/> / <see cref="PerformanceState.MonitorUnavailable"/>
/// with everything null. Failure is data, never an exception: an unreachable/unparseable monitor maps
/// to <see cref="PerformanceState.MonitorUnavailable"/>, so the port never throws. Every measured field
/// follows the monitor's honest contract: <c>null</c> = "not measured", never a fabricated 0.
/// </summary>
public sealed record ServerMetricsReading(
    PerformanceState State,
    double? CpuPctCore,
    long? MemBytes,
    long? RxBps,
    long? TxBps,
    long? IoReadBps,
    long? IoWriteBps,
    long? DiskBytes,
    int? Pids);

/// <summary>One point on a metric's history series: a timestamp and its measured value (for a
/// rollup-tier point the value is the bucket average — the trend line the chart draws).</summary>
public sealed record MetricPoint(DateTimeOffset Ts, double Value);

/// <summary>
/// A windowed history read for one server, as the <see cref="IServerMetrics"/> port returns it: the
/// per-metric time series over the requested range, keyed by the monitor's metric names
/// (<c>cpuPctCore</c>, <c>memBytes</c>, …). <see cref="State"/> is <see cref="PerformanceState.Live"/>
/// when the monitor answered (the series may still be empty — no history recorded in the window, an
/// honest "nothing to show", never an error) or <see cref="PerformanceState.MonitorUnavailable"/> when
/// it couldn't be read. <see cref="Tier"/> is the monitor's <c>raw</c>|<c>rollup</c> tier. Failure is
/// data, never an exception — the port never throws.
/// </summary>
public sealed record ServerMetricsHistory(
    PerformanceState State,
    string Range,
    string? Tier,
    IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> Series);

/// <summary>
/// Live per-server resource metrics — the neutral read that backs the model-facing
/// <c>get_performance</c> tool. A leaf capability that reads the kgsm-monitor (the single source of
/// truth for metrics + history) for one instance; the host supplies the adapter (a unix-socket scrape
/// today). Additive and fails closed: with no monitor reachable a read returns a
/// <see cref="PerformanceState.MonitorUnavailable"/> result, so the assistant composes and boots
/// standalone. Implementations MUST NOT throw — every failure maps to
/// <see cref="PerformanceState.MonitorUnavailable"/>.
/// </summary>
public interface IServerMetrics
{
    /// <summary>Read the latest frame for one instance (the point-in-time snapshot).</summary>
    Task<ServerMetricsReading> GetSnapshotAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Read the windowed history for one instance over <paramref name="range"/>
    /// (<c>1h</c>/<c>24h</c>/<c>7d</c>/<c>30d</c>) — the trend the model asks for over a period.</summary>
    Task<ServerMetricsHistory> GetHistoryAsync(string instance, string range, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IServerMetrics"/> for hosts that have not wired a metrics monitor: every read
/// fails closed as <see cref="PerformanceState.MonitorUnavailable"/>, so embedding the assistant
/// library never breaks DI just because the monitor isn't configured. <c>AddKgsmAssistant</c> registers
/// this with <c>TryAddSingleton</c>; a host that wires the monitor registers a concrete adapter
/// afterward (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableServerMetrics : IServerMetrics
{
    public Task<ServerMetricsReading> GetSnapshotAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerMetricsReading(
            PerformanceState.MonitorUnavailable, null, null, null, null, null, null, null, null));

    public Task<ServerMetricsHistory> GetHistoryAsync(string instance, string range, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerMetricsHistory(
            PerformanceState.MonitorUnavailable, range, null,
            new Dictionary<string, IReadOnlyList<MetricPoint>>()));
}
