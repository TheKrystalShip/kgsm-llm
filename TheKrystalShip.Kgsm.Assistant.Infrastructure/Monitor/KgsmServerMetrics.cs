using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Monitor;

/// <summary>
/// Satisfies the assistant's <see cref="IServerMetrics"/> port by scraping the kgsm-monitor's
/// <c>GET /metrics</c> over its unix-domain socket and picking out the requested instance's frame.
/// The HTTP-over-unix transport reuses the same <see cref="SocketsHttpHandler.ConnectCallback"/>
/// pattern kgsm-lib's watchdog client and kgsm-api's monitor client use.
/// <para>
/// Deliberately independent of the shared <c>Monitor.Contracts</c> NuGet: a small LOCAL DTO
/// deserializes only the fields the tool needs, so the assistant leaf keeps its "depends only on
/// kgsm-lib + a local Ollama" boundary. The capability is additive and degrades gracefully — any
/// failure (connect refused, non-200 incl. the monitor's 503-until-first-tick, timeout, parse error)
/// maps to a <see cref="PerformanceState.MonitorUnavailable"/> reading. Per the port contract this
/// NEVER throws.
/// </para>
/// </summary>
internal sealed class KgsmServerMetrics : IServerMetrics
{
    // Deserialize the monitor's camelCase JSON; only the fields get_performance needs are modelled,
    // nullable exactly where the monitor's wire contract is nullable ("not measured", never 0).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<KgsmServerMetrics> _logger;

    public KgsmServerMetrics(HttpClient http, ILogger<KgsmServerMetrics> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ServerMetricsReading> GetSnapshotAsync(string instance, CancellationToken cancellationToken = default)
    {
        MonitorSnapshot? snapshot;
        try
        {
            using var response = await _http.GetAsync("/metrics", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 503 until the monitor's first tick; any non-2xx is "no data right now" — unavailable.
                _logger.LogDebug("monitor /metrics returned {Status} for '{Instance}'", (int)response.StatusCode, instance);
                return Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            snapshot = await JsonSerializer.DeserializeAsync<MonitorSnapshot>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or JsonException
                                      or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable / slow / unparseable monitor is failure-as-data, never a thrown exception.
            _logger.LogDebug(ex, "monitor scrape failed for '{Instance}'", instance);
            return Unavailable;
        }

        var server = snapshot?.Servers?
            .FirstOrDefault(s => string.Equals(s.Id, instance, StringComparison.OrdinalIgnoreCase));

        // Present in the frame → a running server with live metrics. Absent → the monitor lists
        // running servers only, so the instance simply isn't running (not an error).
        return server is null
            ? new ServerMetricsReading(PerformanceState.NotRunning, null, null, null, null, null, null, null, null)
            : new ServerMetricsReading(
                PerformanceState.Live,
                server.CpuPctCore, server.MemBytes, server.RxBps, server.TxBps,
                server.IoReadBps, server.IoWriteBps, server.DiskBytes, server.Pids);
    }

    public async Task<ServerMetricsHistory> GetHistoryAsync(string instance, string range, CancellationToken cancellationToken = default)
    {
        MonitorHistory? history;
        try
        {
            string url = $"/metrics/history?kind=server&id={Uri.EscapeDataString(instance)}&range={Uri.EscapeDataString(range)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor /metrics/history returned {Status} for '{Instance}'", (int)response.StatusCode, instance);
                return HistoryUnavailable(range);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            history = await JsonSerializer.DeserializeAsync<MonitorHistory>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or JsonException
                                      or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "monitor history scrape failed for '{Instance}'", instance);
            return HistoryUnavailable(range);
        }

        if (history is null)
            return HistoryUnavailable(range);

        // Reshape the monitor's series (each point carries ts+value, plus min/max/n at the rollup tier)
        // into the neutral MetricPoint list the report aggregator + card consume (ts + value only).
        var series = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        if (history.Series is not null)
        {
            foreach (var (metric, points) in history.Series)
            {
                if (points is null || points.Count == 0)
                    continue;
                series[metric] = points.Select(p => new MetricPoint(p.Ts, p.Value)).ToList();
            }
        }

        return new ServerMetricsHistory(PerformanceState.Live, history.Range ?? range, history.Tier, series);
    }

    private static ServerMetricsReading Unavailable =>
        new(PerformanceState.MonitorUnavailable, null, null, null, null, null, null, null, null);

    private static ServerMetricsHistory HistoryUnavailable(string range) =>
        new(PerformanceState.MonitorUnavailable, range, null, new Dictionary<string, IReadOnlyList<MetricPoint>>());

    // --- Local DTO for the monitor's GET /metrics (only the fields get_performance reads) -----------
    // The monitor emits camelCase JSON; nullable fields mirror its honest "null = not measured" contract.
    // Kept private and minimal so the assistant leaf takes no dependency on the Monitor.Contracts package.

    private sealed record MonitorSnapshot(
        [property: JsonPropertyName("servers")] MonitorServer[]? Servers);

    private sealed record MonitorServer(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cpuPctCore")] double CpuPctCore,
        [property: JsonPropertyName("memBytes")] long MemBytes,
        [property: JsonPropertyName("ioReadBps")] long? IoReadBps,
        [property: JsonPropertyName("ioWriteBps")] long? IoWriteBps,
        [property: JsonPropertyName("pids")] int Pids,
        [property: JsonPropertyName("diskBytes")] long? DiskBytes,
        [property: JsonPropertyName("rxBps")] long? RxBps,
        [property: JsonPropertyName("txBps")] long? TxBps);

    // --- Local DTO for the monitor's GET /metrics/history (only the fields the trend card reads) -----
    private sealed record MonitorHistory(
        [property: JsonPropertyName("range")] string? Range,
        [property: JsonPropertyName("tier")] string? Tier,
        [property: JsonPropertyName("series")] Dictionary<string, List<MonitorHistoryPoint>>? Series);

    private sealed record MonitorHistoryPoint(
        [property: JsonPropertyName("ts")] DateTimeOffset Ts,
        [property: JsonPropertyName("value")] double Value);
}
