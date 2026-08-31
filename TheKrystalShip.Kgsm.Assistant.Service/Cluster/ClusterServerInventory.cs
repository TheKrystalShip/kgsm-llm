using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// What servers exist and what games can be installed, answered by the cluster's nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One path, including for a node on this machine.</b> An assistant serving a cluster reaches
/// every node the same way, so an answer about the machine it happens to run on is composed from the
/// same read as an answer about any other. A shortcut for the local case would be a second set of
/// answers about one host, and nothing would say which of the two somebody got.
/// </para>
/// <para>
/// <b>A game the cluster does not offer is unknown, not absent.</b> A detail read for a game no node
/// reported answers null, which the caller words as "not a known game type" — true when every node
/// answered, and short by one machine's catalog when one did not. Which nodes could not be read is
/// carried on the read itself, for the callers that report partial answers.
/// </para>
/// </remarks>
internal sealed class ClusterServerInventory(
    ClusterServers servers,
    ClusterCatalog catalog) : IServerInventory, IInventoryInvalidation
{
    public Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default)
        => servers.GetInstancesAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetInstanceLabelsAsync(CancellationToken cancellationToken = default)
        => servers.GetInstanceLabelsAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetBlueprintNamesAsync(CancellationToken cancellationToken = default)
    {
        FleetCatalog fleet = await catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. fleet.Games.Select(g => g.Id)];
    }

    public async Task<IReadOnlyList<BlueprintSummary>> GetBlueprintCatalogAsync(CancellationToken cancellationToken = default)
    {
        FleetCatalog fleet = await catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. fleet.Games.Select(g => new BlueprintSummary(
                g.Id,
                string.Equals(g.Name, g.Id, StringComparison.Ordinal) ? null : g.Name)),
        ];
    }

    public async Task<BlueprintDetail?> GetBlueprintDetailAsync(
        string blueprintName, CancellationToken cancellationToken = default)
    {
        FleetCatalog fleet = await catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ClusterCatalog.Describe(
            fleet.Games.FirstOrDefault(g => string.Equals(g.Id, blueprintName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Which machine each server is on, from the read the list itself came from.
    /// </summary>
    /// <remarks>
    /// A server two machines both reported is left unattributed rather than assigned to one of them:
    /// the routing table already refuses to act on an ambiguous id, and naming one machine here would
    /// state as a fact the thing that refusal exists because nobody knows.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetInstanceHostsAsync(
        CancellationToken cancellationToken = default)
    {
        FleetServers fleet = await servers.ReadAsync(cancellationToken).ConfigureAwait(false);

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PlacedServer placed in fleet.Found)
        {
            if (byId.TryGetValue(placed.Server.Id, out string? already)
                && !string.Equals(already, placed.Node, StringComparison.Ordinal))
            {
                ambiguous.Add(placed.Server.Id);
                continue;
            }

            byId[placed.Server.Id] = placed.Node;
        }

        foreach (string id in ambiguous)
            byId.Remove(id);

        return byId;
    }

    /// <summary>
    /// Which nodes did not answer, from the reads the lists themselves came from.
    /// </summary>
    /// <remarks>
    /// Both reads are asked, and a node that failed one of them is named once: which of the two it
    /// failed is not a distinction anybody reading a fleet answer needs, and naming it twice reads as
    /// two machines missing.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetUnreachedAsync(CancellationToken cancellationToken = default)
    {
        FleetServers fleet = await servers.ReadAsync(cancellationToken).ConfigureAwait(false);
        FleetCatalog games = await catalog.ReadAsync(cancellationToken).ConfigureAwait(false);

        return [.. fleet.Unreached.Union(games.Unreached, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Forget what the nodes said, so the next read asks them again.
    /// </summary>
    /// <remarks>
    /// The engine on this machine is one of the things that says a server changed, and it says so
    /// about its own node only. The other nodes announce nothing here, which is why what they said is
    /// held for seconds rather than until something invalidates it.
    /// </remarks>
    public void Invalidate()
    {
        servers.Forget();
        catalog.Forget();
    }

    public void InvalidateInstances() => servers.Forget();

    public void InvalidateBlueprints() => catalog.Forget();
}
