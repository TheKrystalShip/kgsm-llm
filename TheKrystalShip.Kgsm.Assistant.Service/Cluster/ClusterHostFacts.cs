using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Ports;

using ApiHost = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// The machine the assistant is asked about, when it serves several.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cluster has no host, so this reads the one node there is and refuses when there are more.</b>
/// The port answers about "the host" — a shape from a leaf that sits beside exactly one engine — and
/// there is no honest way to fold three machines' uptime and memory into it. Naming one silently
/// would answer a question about the wrong computer.
/// </para>
/// <para>
/// The refusal is a state rather than an error, so the tools that read this report an unavailable
/// figure the way they already do for a host that would not answer.
/// </para>
/// </remarks>
internal sealed class ClusterHostFacts(NodeDirectory nodes, NodeApiClient api) : IHostFacts
{
    public async Task<HostFacts> GetAsync(CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, ApiHost? host, string? why) =
            await TheOneNodeAsync(cancellationToken).ConfigureAwait(false);
        if (node is null || host is null)
            return new HostFacts(FactsState.Unavailable, null, null, null, null, null, null, why);

        DiskCapacity? root = host.Disks?.FirstOrDefault(d => d.Mount == "/");

        return new HostFacts(
            FactsState.Available,
            Uptime: host.Identity?.StartedAt is { } since
                ? $"up since {since.ToLocalTime():yyyy-MM-dd HH:mm}"
                : null,
            Load: host.Load is { } load
                ? new HostLoad($"{load.One:0.00}", $"{load.Five:0.00}", $"{load.Fifteen:0.00}")
                : null,
            Memory: host.Mem is { } mem
                ? new HostMemory(
                    Total: Gib(mem.Total),
                    Used: Gib(mem.Used),
                    Free: Gib(mem.Total - mem.Used),
                    // Free and available are different numbers and the node reports both: free is what
                    // nothing holds, available is what starting something could actually get, which is
                    // larger by the reclaimable page cache. A node too old to carry it says nothing
                    // rather than repeating free under the other name.
                    Available: mem.Available is { } spare ? Gib(spare) : string.Empty)
                : null,
            Disk: root is { Total: > 0 }
                ? new HostDisk(
                    UsedPercent: (int)Math.Round(root.Used / root.Total * 100),
                    Size: Gib(root.Total),
                    Available: Gib(root.Total - root.Used),
                    Mount: root.Mount)
                : null,
            // Neither is a fact a node publishes about itself.
            ExternalIp: null,
            RebootRequired: null);
    }

    /// <summary>
    /// What is bound on the machine, and where two claimants want the same port.
    /// </summary>
    /// <remarks>
    /// The two scans carry their own states because they are two scans, exactly as the node reports
    /// them: an unread conflict scan served as an empty list looks like a healthy machine, and no
    /// conflicts is the ordinary answer.
    /// </remarks>
    public async Task<HostPortUsage> GetPortUsageAsync(CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, ApiHost? _, string? why) =
            await TheOneNodeAsync(cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Unreadable(why);

        NodeResult<HostPortsDto> read = await api.GetAsync(
            node, $"/api/v1/hosts/{Uri.EscapeDataString(node.MemberId)}/ports",
            ApiContractsJson.Default.HostPortsDto, cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } ports)
            return Unreadable($"{node.MemberId} {read.Failure ?? "answered nothing"}");

        return new HostPortUsage(
            State(ports.State),
            [.. ports.UsedPorts.Select(p => new HostPortEntry(p.Port, p.Protocol, p.Process, p.Instance))],
            State(ports.ConflictState),
            [
                .. ports.Conflicts.Select(c => new PortConflictEntry(
                    c.Port, c.Protocol, c.Instance, c.Other, c.OtherIsInstance)),
            ]);
    }

    /// <summary>
    /// The cluster's single node, when there is exactly one.
    /// </summary>
    /// <remarks>
    /// More than one and there is no answer to give: this port describes a machine, and the caller
    /// asked about the fleet. Fewer than one and there is nothing to ask.
    /// </remarks>
    private async Task<(ClusterNode?, ApiHost?, string?)> TheOneNodeAsync(CancellationToken ct)
    {
        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);

        if (known.Count == 0)
            return (null, null, "this assistant serves a cluster with no machines in it");

        if (known.Count > 1)
        {
            // Named, because the question is answerable once somebody says which machine — and a
            // caller told only that the read failed would go looking for a broken one.
            return (null, null,
                "this assistant serves " + known.Count + " machines ("
                + string.Join(", ", known.Select(n => n.MemberId))
                + ") and this reading is about one machine, so it needs to be asked of a named one");
        }

        ClusterNode node = known[0];
        NodeResult<ApiHost> read = await api.GetAsync(
            node, $"/api/v1/hosts/{Uri.EscapeDataString(node.MemberId)}",
            ApiContractsJson.Default.Host, ct).ConfigureAwait(false);

        return read.Answered
            ? (node, read.Body, null)
            : (node, null, $"{node.MemberId} {read.Failure ?? "answered nothing"}");
    }

    private static FactsState State(string reported) =>
        string.Equals(reported, "available", StringComparison.Ordinal)
            ? FactsState.Available
            : FactsState.Unavailable;

    private static HostPortUsage Unreadable(string? why) =>
        new(FactsState.Unavailable, [], FactsState.Unavailable, [], why);

    /// <summary>A GiB figure as a person reads one. The node reports these already in GiB.</summary>
    private static string Gib(double value) => $"{value:0.#}G";
}
