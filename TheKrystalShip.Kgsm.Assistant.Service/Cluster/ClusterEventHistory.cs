using System.Globalization;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// What happened, across the cluster's machines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each node keeps its own log with its own cursor.</b> There is no shared ordering to page
/// through, so a fleet-wide read takes a bounded recent window from every node, merges it and sorts
/// newest-first — the same strategy the panel uses for the same reason. Asking for the last hundred
/// events across three machines therefore reads a hundred from each and keeps the hundred newest,
/// which is the honest way to answer a question about a set of independent logs.
/// </para>
/// <para>
/// <b>A node that could not be read makes the whole answer partial, and it says so.</b> A history
/// short by one machine's events looks exactly like a quieter cluster, and nothing in a list of rows
/// says which — so a read that lost a node reports the journal as unavailable rather than serving
/// what it did get as though it were everything.
/// </para>
/// </remarks>
internal sealed class ClusterEventHistory(
    ClusterServers servers, NodeDirectory nodes, NodeApiClient api) : IEventHistory
{
    public async Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default)
    {
        // Naming a server narrows the read to the one machine holding it: the others cannot have
        // events about a server they do not run, and asking them would be three reads for one answer.
        IReadOnlyList<ClusterNode> asking;
        if (!string.IsNullOrWhiteSpace(instance))
        {
            (ClusterNode? node, string? _) = await servers.RouteAsync(instance!, cancellationToken)
                .ConfigureAwait(false);
            if (node is null)
                return new EventHistoryReading(AuditReadState.JournalUnavailable, []);
            asking = [node];
        }
        else
        {
            asking = await nodes.NodesAsync(cancellationToken).ConfigureAwait(false);
        }

        string query = $"?limit={Math.Max(1, limit)}";
        if (!string.IsNullOrWhiteSpace(instance))
            query += $"&serverId={Uri.EscapeDataString(instance!)}";
        if (sinceMs is { } since)
        {
            query += "&since=" + Uri.EscapeDataString(
                DateTimeOffset.FromUnixTimeMilliseconds(since).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        IEnumerable<Task<NodeResult<AuditPage>>> reads = asking.Select(node =>
            api.GetAsync(node, $"/api/v1/audit{query}", ApiContractsJson.Default.AuditPage, cancellationToken));

        var rows = new List<AuditEventRow>();
        bool whole = true;

        foreach (NodeResult<AuditPage> read in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (!read.Answered || read.Body is null)
            {
                whole = false;
                continue;
            }

            rows.AddRange(read.Body.Data.Select(Row));
        }

        if (!whole)
            return new EventHistoryReading(AuditReadState.JournalUnavailable, []);

        return new EventHistoryReading(
            AuditReadState.Available,
            [.. rows.OrderByDescending(r => r.Ts).Take(Math.Max(1, limit))]);
    }

    /// <summary>
    /// One node's audit row as an event.
    /// </summary>
    /// <remarks>
    /// The actor's name, not the whole actor: a row carries who did it and through what, and both are
    /// already separate fields here. A row with no target names no server, which is a real fleet-level
    /// event rather than a missing field.
    /// </remarks>
    private static AuditEventRow Row(AuditRecord record) => new(
        record.Id,
        record.Ts,
        record.Action,
        record.ServerId ?? record.Target?.Id,
        string.IsNullOrWhiteSpace(record.Actor.Name) ? null : record.Actor.Name,
        record.Origin);
}
