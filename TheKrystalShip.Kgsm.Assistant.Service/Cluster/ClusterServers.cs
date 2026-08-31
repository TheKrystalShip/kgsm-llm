using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>One server as a node reports it.</summary>
/// <remarks>
/// Only what this assistant reads. A node's row carries live metrics, update state and more; taking
/// the whole shape would make every field a node adds a compile break here.
/// </remarks>
public sealed record NodeServer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("blueprint")] string? Blueprint);

[JsonSerializable(typeof(List<NodeServer>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class NodeApiJson : JsonSerializerContext;

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
/// is carried on <see cref="Unreached"/> for a caller that can say so.
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
    ILogger<ClusterServers> logger)
{
    /// <summary>Where a server id was last seen, so an action reaches the machine holding it.</summary>
    private readonly Dictionary<string, List<string>> _where = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    /// <summary>The nodes the last read could not reach, for a caller that reports partial answers.</summary>
    public IReadOnlyList<string> Unreached { get; private set; } = [];

    /// <summary>
    /// The node holding a server, or <see langword="null"/> when it is not known here. More than one
    /// means the id is ambiguous and no action may be routed on it.
    /// </summary>
    public IReadOnlyList<string> NodesHolding(string instanceId)
    {
        lock (_gate)
            return _where.TryGetValue(instanceId, out List<string>? found) ? [.. found] : [];
    }

    /// <summary>Every server in the cluster as id → game type, which is what a list of servers is for.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<(string Node, NodeServer Server)> servers = await ReadAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((_, NodeServer server) in servers)
            byId[server.Id] = server.Blueprint ?? "";
        return byId;
    }

    /// <summary>Every server as id → the label a person calls it by, never blank.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetInstanceLabelsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<(string Node, NodeServer Server)> servers = await ReadAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((_, NodeServer server) in servers)
            byId[server.Id] = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name!;
        return byId;
    }

    /// <summary>
    /// Every server in the cluster, with the node it is on, and the routing table refreshed from what
    /// was actually answered.
    /// </summary>
    private async Task<IReadOnlyList<(string Node, NodeServer Server)>> ReadAsync(CancellationToken ct)
    {
        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);

        var found = new List<(string, NodeServer)>();
        var unreached = new List<string>();

        IEnumerable<Task<NodeResult<List<NodeServer>>>> reads = known.Select(node =>
            api.GetAsync(node, "/api/v1/servers", NodeApiJson.Default.ListNodeServer, ct));

        foreach (NodeResult<List<NodeServer>> result in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (!result.Answered)
            {
                unreached.Add($"{result.Node} ({result.Failure})");
                continue;
            }

            foreach (NodeServer server in result.Body ?? [])
                found.Add((result.Node, server));
        }

        lock (_gate)
        {
            // Rebuilt from what answered, and only from it: a node that did not answer keeps whatever
            // was last known about it rather than having its servers forgotten, because forgetting
            // would turn "I could not ask" into "there is nothing there".
            foreach ((string node, NodeServer server) in found)
            {
                if (!_where.TryGetValue(server.Id, out List<string>? holders))
                    _where[server.Id] = holders = [];
                if (!holders.Contains(node, StringComparer.Ordinal))
                    holders.Add(node);
            }
        }

        Unreached = unreached;
        if (unreached.Count > 0)
            logger.LogInformation("cluster servers: could not read {Nodes}", string.Join(", ", unreached));

        return found;
    }
}
