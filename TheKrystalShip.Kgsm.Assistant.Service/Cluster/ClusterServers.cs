using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>One server and the machine it is on.</summary>
public sealed record PlacedServer(string Node, Server Server);

/// <summary>Where an action on one server goes, or why it goes nowhere.</summary>
/// <param name="Node">The node holding it, when exactly one does.</param>
/// <param name="Failure">Why it could not be routed, worded for the person who asked.</param>
public sealed record NodeRoute(ClusterNode? Node, string? Failure);

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
    ClusterCatalog catalog,
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
    /// The node an action on this server has to be sent to, or why it cannot be sent anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unknown is told apart from unreachable.</b> A server nobody reported might not exist, or might
    /// be on the machine that did not answer — and those are fixed by different things, so a node that
    /// could not be read is named in the refusal rather than left to make the server look absent.
    /// </para>
    /// <para>
    /// <b>Two nodes holding one id refuses.</b> Ids are minted per install and are usually distinct
    /// across a cluster, but nothing enforces it; picking one would send somebody's action to a machine
    /// they did not name, and no wording afterwards recovers that.
    /// </para>
    /// </remarks>
    public async Task<NodeRoute> RouteAsync(string instanceId, CancellationToken ct = default)
    {
        FleetServers fleet = await ReadAsync(ct).ConfigureAwait(false);

        List<PlacedServer> holders =
            [.. fleet.Found.Where(p => string.Equals(p.Server.Id, instanceId, StringComparison.OrdinalIgnoreCase))];

        if (holders.Count > 1)
        {
            return new NodeRoute(null,
                $"'{instanceId}' exists on more than one machine ({string.Join(", ", holders.Select(h => h.Node))}), "
                + "so it is not clear which one this is about. Nothing was done.");
        }

        if (holders.Count == 0)
        {
            return new NodeRoute(null, fleet.Unreached.Count > 0
                ? $"there is no server called '{instanceId}' on any machine that answered, and "
                  + $"{string.Join("; ", fleet.Unreached)} — so it may be there."
                : $"there is no server called '{instanceId}' on any machine in this cluster.");
        }

        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);
        ClusterNode? node = known.FirstOrDefault(n => string.Equals(n.MemberId, holders[0].Node, StringComparison.Ordinal));

        return node is null
            ? new NodeRoute(null, $"'{holders[0].Node}' is no longer a node this assistant can reach.")
            : new NodeRoute(node, null);
    }

    /// <summary>
    /// Which node a new server of this game should be installed on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is nothing to route to: the server does not exist yet, so its id looks up nothing. What
    /// decides the machine is which of them offers the game — and when more than one does, nothing
    /// here decides, because the cluster holds no placement policy and choosing would put somebody's
    /// server on a machine they never named.
    /// </para>
    /// <para>
    /// The refusal names the candidates, which is what turns it into a question a person can answer.
    /// </para>
    /// </remarks>
    public async Task<NodeRoute> RouteForInstallAsync(string blueprint, CancellationToken ct = default)
    {
        FleetCatalog fleet = await catalog.ReadAsync(ct).ConfigureAwait(false);

        List<string> offering =
            [.. fleet.OfferedBy(blueprint).OrderBy(n => n, StringComparer.Ordinal)];

        if (offering.Count > 1)
        {
            return new NodeRoute(null,
                $"{string.Join(" and ", offering)} can both install {blueprint}, and nothing here picks "
                + "between machines. Ask which one it should go on.");
        }

        if (offering.Count == 0)
        {
            return new NodeRoute(null, fleet.Unreached.Count > 0
                ? $"no machine that answered can install '{blueprint}', and {string.Join("; ", fleet.Unreached)}."
                : $"no machine in this cluster can install '{blueprint}'.");
        }

        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);
        ClusterNode? node = known.FirstOrDefault(n => string.Equals(n.MemberId, offering[0], StringComparison.Ordinal));

        return node is null
            ? new NodeRoute(null, $"'{offering[0]}' is no longer a node this assistant can reach.")
            : new NodeRoute(node, null);
    }

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
