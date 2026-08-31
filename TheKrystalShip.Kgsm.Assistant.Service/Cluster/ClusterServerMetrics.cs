using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// How hard a server on another machine is working, as that machine's monitor measured it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stopped server has no reading, and that is not a failure.</b> Every figure in a live sample
/// exists only while a process does, so a node reports none for a server that is not running — which
/// is why the absence of a metrics block is told apart from a monitor that could not be asked. The
/// first is the ordinary state of a stopped server; the second is an outage.
/// </para>
/// <para>
/// <b>Disk is the exception and rides along regardless.</b> The space an installed server occupies is
/// a property of its files rather than of a run, so a node carries it for a stopped server too.
/// </para>
/// </remarks>
internal sealed class ClusterServerMetrics(ClusterServers servers, NodeApiClient api) : IServerMetrics
{
    public async Task<ServerMetricsReading> GetSnapshotAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? _) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Unreadable;

        NodeResult<Server> read = await api.GetAsync(
            node, $"/api/v1/servers/{Uri.EscapeDataString(instance)}", ApiContractsJson.Default.Server,
            cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } server)
            return Unreadable;

        if (server.Metrics is not { } m)
        {
            // The node answered and carried no sample. Disk still stands on its own, so a stopped
            // server reports the space it occupies rather than nothing at all.
            return new ServerMetricsReading(
                PerformanceState.NotRunning, null, null, null, null, null, null, server.DiskBytes, null);
        }

        return new ServerMetricsReading(
            PerformanceState.Live,
            m.CpuPctCore, m.MemBytes, m.RxBps, m.TxBps,
            m.IoReadBps, m.IoWriteBps, server.DiskBytes ?? m.DiskBytes, m.Pids);
    }

    public async Task<ServerMetricsHistory> GetHistoryAsync(
        string instance, string range, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? _) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return NoHistory(range);

        NodeResult<MetricsHistoryResponse> read = await api.GetAsync(
            node,
            $"/api/v1/servers/{Uri.EscapeDataString(instance)}/metrics/history?range={Uri.EscapeDataString(range)}",
            ApiContractsJson.Default.MetricsHistoryResponse, cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } history)
            return NoHistory(range);

        // The node's own range and tier, echoed rather than the range that was asked for: a node that
        // served a coarser tier than requested has said so, and repeating the request back would hide it.
        return new ServerMetricsHistory(
            PerformanceState.Live,
            history.Range,
            history.Tier,
            history.Series.ToDictionary(
                s => s.Key,
                s => (IReadOnlyList<MetricPoint>)[.. s.Value.Select(p => new MetricPoint(p.Ts, p.Value))],
                StringComparer.Ordinal));
    }

    /// <summary>The node could not be asked, which is not the same as a server that is not running.</summary>
    private static ServerMetricsReading Unreadable =>
        new(PerformanceState.MonitorUnavailable, null, null, null, null, null, null, null, null);

    private static ServerMetricsHistory NoHistory(string range) =>
        new(PerformanceState.MonitorUnavailable, range, null,
            new Dictionary<string, IReadOnlyList<MetricPoint>>(StringComparer.Ordinal));
}
