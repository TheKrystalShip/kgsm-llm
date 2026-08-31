using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>One server and the machine it is on.</summary>
public sealed record PlacedServer(string Node, Server Server);

/// <summary>What the fleet answered: the servers found, and the nodes that did not answer.</summary>
/// <param name="Found">Every server that was reported, each carrying the node reporting it.</param>
/// <param name="Unreached">The nodes that could not be read, each with why — never an empty
/// explanation, because a fleet answer missing a machine looks exactly like a fleet with fewer
/// machines.</param>
public sealed record FleetServers(IReadOnlyList<PlacedServer> Found, IReadOnlyList<string> Unreached);

/// <summary>
/// Which servers the cluster has, and which machine each one is on.
/// </summary>
/// <remarks>
/// <para>
/// The port's shape does not change, and that is deliberate: a server is addressed by its id
/// everywhere — every tool argument, every path, every event — and teaching the model a second,
/// node-qualified way to name one would change what it has to say to do anything. So the routing table
/// lives here, and an id still means what it always meant.
/// </para>
/// <para>
/// <b>A node that cannot be reached is named, not dropped.</b> A fleet answer missing a machine looks
/// exactly like a fleet with fewer machines, and nothing in a list says which it is. What is missing
/// is carried on <see cref="FleetServers.Unreached"/> for a caller that can say so.
/// </para>
/// <para>
/// <b>Two nodes with the same server id is a question, not a winner.</b> Ids are minted per install
/// and are usually distinct across a cluster, but nothing enforces it — and picking one silently
/// would send an action to a machine nobody named. Both are kept, and the routing table reports the
/// ambiguity.
/// </para>
/// </remarks>
public sealed class ClusterServers(
    NodeDirectory nodes,
    NodeApiClient api,
    IInvocationContext invocation,
    AssistantClusterSettings settings,
    ILogger<ClusterServers> logger)
{
    /// <summary>Where a server id was last seen, so an action reaches the machine holding it.</summary>
    private readonly Dictionary<string, List<string>> _where = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    private readonly FleetSnapshot<FleetServers> _snapshot = new(settings.FleetWindow);

    /// <summary>
    /// The node holding a server, or nothing when it is not known here. More than one means the id is
    /// ambiguous and no action may be routed on it.
    /// </summary>
    public IReadOnlyList<string> NodesHolding(string instanceId)
    {
        lock (_gate)
            return _where.TryGetValue(instanceId, out List<string>? found) ? [.. found] : [];
    }

    /// <summary>Every server in the cluster as id → game type, which is what a list of servers is for.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken ct = default)
    {
        FleetServers fleet = await ReadAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlacedServer placed in fleet.Found)
            byId[placed.Server.Id] = placed.Server.Blueprint;
        return byId;
    }

    /// <summary>Every server as id → the label a person calls it by, never blank.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetInstanceLabelsAsync(CancellationToken ct = default)
    {
        FleetServers fleet = await ReadAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlacedServer placed in fleet.Found)
            byId[placed.Server.Id] =
                string.IsNullOrWhiteSpace(placed.Server.Name) ? placed.Server.Id : placed.Server.Name;
        return byId;
    }

    /// <summary>Forget what the fleet last said, so the next read asks every node again.</summary>
    public void Forget() => _snapshot.Clear();

    /// <summary>
    /// Every server in the cluster, with the node it is on, and the routing table refreshed from what
    /// was actually answered.
    /// </summary>
    public Task<FleetServers> ReadAsync(CancellationToken ct = default) =>
        _snapshot.ReadAsync(invocation.Current?.Handle ?? "", () => AskAsync(ct));

    private async Task<FleetServers> AskAsync(CancellationToken ct)
    {
        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);

        var found = new List<PlacedServer>();
        var unreached = new List<string>();

        IEnumerable<Task<NodeResult<List<Server>>>> reads = known.Select(node =>
            api.GetAsync(node, "/api/v1/servers", ApiContractsJson.Default.ListServer, ct));

        foreach (NodeResult<List<Server>> result in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (!result.Answered)
            {
                unreached.Add($"{result.Node} ({result.Failure})");
                continue;
            }

            foreach (Server server in result.Body ?? [])
                found.Add(new PlacedServer(result.Node, server));
        }

        lock (_gate)
        {
            // Rebuilt from what answered, and only from it: a node that did not answer keeps whatever
            // was last known about it rather than having its servers forgotten, because forgetting
            // would turn "I could not ask" into "there is nothing there".
            foreach (PlacedServer placed in found)
            {
                if (!_where.TryGetValue(placed.Server.Id, out List<string>? holders))
                    _where[placed.Server.Id] = holders = [];
                if (!holders.Contains(placed.Node, StringComparer.Ordinal))
                    holders.Add(placed.Node);
            }
        }

        if (unreached.Count > 0)
            logger.LogInformation("cluster servers: could not read {Nodes}", string.Join(", ", unreached));

        return new FleetServers(found, unreached);
    }
}
