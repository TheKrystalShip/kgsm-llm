using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>The cluster's catalog: every game installable anywhere in it, and what each one needs.</summary>
/// <param name="Games">One entry per game, whichever nodes offer it.</param>
/// <param name="Unreached">The nodes that could not be read, each with why.</param>
public sealed record FleetCatalog(IReadOnlyList<LibraryEntry> Games, IReadOnlyList<string> Unreached);

/// <summary>
/// What the cluster can install, read from every node's own catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>A game is offered by the cluster when any node offers it.</b> Blueprints are shipped with the
/// engine, so nodes overwhelmingly agree; where they do not, the union is the honest answer to "what
/// can I run?" and the machine it can run on is settled when an install is actually proposed.
/// </para>
/// <para>
/// <b>Two nodes describing one game are not merged field by field.</b> A blueprint is one file and a
/// node either has it or does not, so the first node to carry a game describes it whole. Filling one
/// node's blanks from another would compose an answer no machine would give — a game claimed to
/// support banning because a different machine's copy of the blueprint declares it.
/// </para>
/// </remarks>
public sealed class ClusterCatalog(
    NodeDirectory nodes,
    NodeApiClient api,
    IInvocationContext invocation,
    AssistantClusterSettings settings,
    ILogger<ClusterCatalog> logger)
{
    private readonly FleetSnapshot<FleetCatalog> _snapshot = new(settings.FleetWindow);

    /// <summary>Forget what the fleet last said, so the next read asks every node again.</summary>
    public void Forget() => _snapshot.Clear();

    /// <summary>Every game the cluster can install, and the nodes that could not be asked.</summary>
    public Task<FleetCatalog> ReadAsync(CancellationToken ct = default) =>
        _snapshot.ReadAsync(invocation.Current?.Handle ?? "", () => AskAsync(ct));

    private async Task<FleetCatalog> AskAsync(CancellationToken ct)
    {
        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(ct).ConfigureAwait(false);

        var games = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var unreached = new List<string>();

        IEnumerable<Task<NodeResult<List<LibraryEntry>>>> reads = known.Select(node =>
            api.GetAsync(node, "/api/v1/library", ApiContractsJson.Default.ListLibraryEntry, ct));

        foreach (NodeResult<List<LibraryEntry>> result in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (!result.Answered)
            {
                unreached.Add($"{result.Node} ({result.Failure})");
                continue;
            }

            foreach (LibraryEntry game in result.Body ?? [])
                if (game.Id.Length > 0)
                    games.TryAdd(game.Id, game);
        }

        if (unreached.Count > 0)
            logger.LogInformation("cluster catalog: could not read {Nodes}", string.Join(", ", unreached));

        return new FleetCatalog(
            [.. games.Values.OrderBy(g => g.Id, StringComparer.OrdinalIgnoreCase)], unreached);
    }

    /// <summary>
    /// One game as the assistant describes it, or <see langword="null"/> when the cluster offers no
    /// such game.
    /// </summary>
    /// <remarks>
    /// A null here means the cluster's catalog does not contain it. When a node could not be read the
    /// catalog is short by that node's games, which is why an unreachable node is carried on the read
    /// rather than being swallowed into an answer that reads as "no such game".
    /// </remarks>
    public static BlueprintDetail? Describe(LibraryEntry? game)
    {
        if (game is null)
            return null;

        // kgsm-api answers the id when a game declares no display name of its own, and the assistant's
        // shape carries the absence instead — so the fallback happens once, where the label is read.
        string? display = string.Equals(game.Name, game.Id, StringComparison.Ordinal) ? null : game.Name;

        var verbs = new List<string>(3);
        if (game.Moderation.Kick) verbs.Add("kick");
        if (game.Moderation.Ban) verbs.Add("ban");
        if (game.Moderation.Unban) verbs.Add("unban");

        return new BlueprintDetail(
            Name: game.Id,
            DisplayName: string.IsNullOrWhiteSpace(display) ? null : display,
            Description: string.IsNullOrWhiteSpace(game.Description) ? null : game.Description,
            Ports: [.. game.Ports.Select(PortText)],
            Kind: game.Type,
            SteamAccountRequired: game.IsSteamAccountRequired,
            MaxPlayers: game.Specs.MaxPlayers,
            MinRamMb: game.Specs.MinRamMb,
            RecommendedRamMb: game.Specs.RecommendedRamMb,
            BaseDiskMb: game.Specs.BaseDiskMb,
            ModerationVerbs: verbs);
    }

    /// <summary>A port range as the ecosystem writes one: <c>26900:26903/tcp</c>, a single port
    /// without its range.</summary>
    private static string PortText(LibraryPort port) =>
        port.Start == port.End
            ? $"{port.Start}/{port.Proto}"
            : $"{port.Start}:{port.End}/{port.Proto}";
}
