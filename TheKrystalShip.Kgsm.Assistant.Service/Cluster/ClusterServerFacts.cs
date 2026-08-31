using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// Reading a server that is on another machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every reading is the node's, and an unread one says so.</b> A node that could not be asked
/// yields <see cref="FactsState.Unavailable"/> and an empty payload, never an empty payload on its
/// own — an unreachable machine and a server with no backups are the same shape otherwise, and the
/// second is a fact while the first is the absence of one.
/// </para>
/// <para>
/// <b>Some readings do not exist over a wire and are absent rather than approximated.</b> A process
/// id and an install path are the node's own internals and it publishes neither; a run history is not
/// a read a node serves, since its console surface holds the current run alone. Each is null or
/// unavailable, which is what the callers already do with a figure nobody measured.
/// </para>
/// </remarks>
internal sealed class ClusterServerFacts(
    ClusterServers servers, NodeDirectory nodes, NodeApiClient api) : IServerFacts
{
    public async Task<BackupListing> GetBackupsAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, ServerBackupList? list) = await OnNodeAsync(
            instance, "/backups", ApiContractsJson.Default.ServerBackupList, cancellationToken)
            .ConfigureAwait(false);

        if (node is null || list is null)
            return new BackupListing(FactsState.Unavailable, []);

        return new BackupListing(FactsState.Available,
        [
            .. list.Backups.Select(b => new BackupEntry(
                Id: b.Name,
                Version: b.Version,
                CreatedAt: b.CreatedAt,
                SizeBytes: b.SizeBytes ?? 0,
                Consistency: b.Consistency,
                Contents: b.Sources ?? [],
                FileCount: b.FileCount ?? 0)),
        ]);
    }

    public async Task<VersionFacts> GetVersionAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        Server? server = await ReadServerAsync(instance, cancellationToken).ConfigureAwait(false);
        if (server is null)
            return new VersionFacts(FactsState.Unavailable, null, null, null);

        return new VersionFacts(
            FactsState.Available,
            Installed: string.IsNullOrWhiteSpace(server.Version) ? null : server.Version,
            Latest: server.LatestVersion,
            UpdateAvailable: server.UpdateAvailable,
            CheckedAt: server.UpdateCheckedAt);
    }

    /// <summary>
    /// One server's whole record, as its node holds it.
    /// </summary>
    /// <remarks>
    /// The process id and the install directory are absent on purpose: a node publishes neither, and a
    /// path on a machine somebody is not on is not a thing they can act on. Null is what the shape
    /// already means by "the engine did not say".
    /// </remarks>
    public async Task<InstanceStatusFacts> GetStatusAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        Server? server = await ReadServerAsync(instance, cancellationToken).ConfigureAwait(false);
        if (server is null)
            return new InstanceStatusFacts(
                FactsState.Unavailable, null, null, null, null, null, null, null, [], null, null, null, 0);

        return new InstanceStatusFacts(
            State: FactsState.Available,
            Running: string.Equals(server.Status, ServerStatus.Unknown, StringComparison.Ordinal)
                ? null
                : !string.Equals(server.Status, ServerStatus.Stopped, StringComparison.Ordinal),
            Pid: null,
            StartedAt: server.StartedAt,
            Blueprint: server.Blueprint,
            Runtime: server.Runtime,
            Directory: null,
            DiskUsage: server.DiskBytes is { } bytes ? Bytes(bytes) : null,
            Ports: server.Ports ?? [],
            InstalledVersion: string.IsNullOrWhiteSpace(server.Version) ? null : server.Version,
            LatestVersion: server.LatestVersion,
            UpdateAvailable: server.UpdateAvailable,
            BackupCount: server.BackupCount ?? 0,
            LibraryState: LibraryStateOf(server.LibraryState));
    }

    /// <summary>
    /// The instance's KGSM configuration, every key the engine will accept a change to.
    /// </summary>
    /// <remarks>
    /// A node serves the editable keys and nothing else — it withholds the identity and path keys the
    /// engine refuses anyway — so every key that arrives here is settable, and saying so is a
    /// measurement rather than an assumption. What a reader is told it can change is exactly what the
    /// write path takes.
    /// </remarks>
    public async Task<InstanceConfigFacts> GetConfigAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, ServerConfig? config) = await OnNodeAsync(
            instance, "/config", ApiContractsJson.Default.ServerConfig, cancellationToken).ConfigureAwait(false);

        if (node is null || config is null)
            return new InstanceConfigFacts(FactsState.Unavailable, []);

        return new InstanceConfigFacts(FactsState.Available,
        [
            .. config.Values
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new InstanceSetting(kv.Key, kv.Value, Settable: true)),
        ]);
    }

    public async Task<NoteFacts> GetNoteAsync(string instance, CancellationToken cancellationToken = default)
    {
        Server? server = await ReadServerAsync(instance, cancellationToken).ConfigureAwait(false);
        if (server is null)
            return new NoteFacts(FactsState.Unavailable, null, null, null);

        // The note rides the server's own row, so this costs no second call. A server with no note
        // carries none, which is the measured "nothing written" rather than a read that failed.
        return new NoteFacts(
            FactsState.Available,
            server.Note?.Body,
            server.Note?.UpdatedBy,
            server.Note?.UpdatedAt?.ToString("O"));
    }

    /// <summary>
    /// Who is on each server in the cluster.
    /// </summary>
    /// <remarks>
    /// A roster is per server, so this is one call per server rather than one for the fleet. A server
    /// whose roster could not be read is left out of the reading entirely rather than listed as empty:
    /// nobody-is-here and could-not-look are the two answers this shape exists to keep apart.
    /// </remarks>
    public async Task<PresenceReading> GetPresenceAsync(CancellationToken cancellationToken = default)
    {
        FleetServers fleet = await servers.ReadAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<Task<(PlacedServer Placed, NodeResult<PlayersResponse> Roster)>> reads =
            fleet.Found.Select(async placed => (placed, await api.GetAsync(
                Address(known, placed), $"/api/v1/servers/{Segment(placed.Server.Id)}/players",
                ApiContractsJson.Default.PlayersResponse, cancellationToken).ConfigureAwait(false)));

        var found = new List<InstancePresence>();
        foreach ((PlacedServer placed, NodeResult<PlayersResponse> roster)
                 in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (!roster.Answered || roster.Body is not { } people)
                continue;

            found.Add(new InstancePresence(
                placed.Server.Id,
                // The node states the mechanism in the supervisor's own vocabulary, so it is carried
                // rather than derived. Collapsing `rcon` into `log` would claim that an empty roster
                // means nobody is connected, when a polled reading cannot see somebody who joined and
                // left between two polls.
                MechanismOf(people.Mechanism),
                [
                    .. people.Players
                        .Where(p => p.Status == PlayerStatus.online)
                        .Select(p => new PlayerEntry(p.PlayerId, p.PlayerName)),
                ]));
        }

        return new PresenceReading(FactsState.Available, found);
    }

    /// <summary>
    /// Which servers start when their machine boots.
    /// </summary>
    /// <remarks>
    /// Autostart is a per-server setting, so this is a call per server. A server whose settings could
    /// not be read contributes nothing rather than a false: not knowing is not the same as knowing it
    /// is off, and this shape lists only what is on.
    /// </remarks>
    public async Task<AutostartReading> GetAutostartAsync(CancellationToken cancellationToken = default)
    {
        FleetServers fleet = await servers.ReadAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ClusterNode> known = await nodes.NodesAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<Task<(PlacedServer Placed, NodeResult<ServerSettings> Settings)>> reads =
            fleet.Found.Select(async placed => (placed, await api.GetAsync(
                Address(known, placed), $"/api/v1/servers/{Segment(placed.Server.Id)}/settings",
                ApiContractsJson.Default.ServerSettings, cancellationToken).ConfigureAwait(false)));

        var enabled = new List<string>();
        foreach ((PlacedServer placed, NodeResult<ServerSettings> settings)
                 in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (settings.Answered && settings.Body?.Autostart == true)
                enabled.Add(placed.Server.Id);
        }

        enabled.Sort(StringComparer.OrdinalIgnoreCase);
        return new AutostartReading(FactsState.Available, enabled);
    }

    public async Task<ConsoleTail> GetConsoleTailAsync(
        string instance, int lines, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, ConsoleScrollback? console) = await OnNodeAsync(
            instance, $"/console?tail={lines}", ApiContractsJson.Default.ConsoleScrollback, cancellationToken)
            .ConfigureAwait(false);

        return node is null || console is null
            ? new ConsoleTail(FactsState.Unavailable, [])
            : new ConsoleTail(FactsState.Available, console.Lines);
    }

    /// <summary>
    /// The runs of a server's console, which a node does not serve.
    /// </summary>
    /// <remarks>
    /// A node's console surface holds the run in progress; earlier runs are the supervisor's own index
    /// on that machine and there is no read for them. Reported unavailable rather than as an empty
    /// list, because an empty list of runs is a claim that a server has never run — and the callers
    /// that ask about a crash would read it as one.
    /// </remarks>
    public Task<ConsoleRuns> GetConsoleRunsAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleRuns(FactsState.Unavailable, []));

    /// <inheritdoc cref="GetConsoleRunsAsync"/>
    public Task<ConsoleTail> GetConsoleRunTailAsync(
        string instance, int runIndex, int lines, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleTail(FactsState.Unavailable, []));

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<Server?> ReadServerAsync(string instance, CancellationToken ct)
    {
        (ClusterNode? _, Server? server) = await OnNodeAsync(
            instance, string.Empty, ApiContractsJson.Default.Server, ct).ConfigureAwait(false);
        return server;
    }

    /// <summary>Read one of a server's sub-resources from whichever node holds it.</summary>
    private async Task<(ClusterNode?, T?)> OnNodeAsync<T>(
        string instance, string suffix, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape,
        CancellationToken ct)
    {
        (ClusterNode? node, string? _) = await servers.RouteAsync(instance, ct).ConfigureAwait(false);
        if (node is null)
            return (null, default);

        NodeResult<T> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}{suffix}", shape, ct).ConfigureAwait(false);

        return read.Answered ? (node, read.Body) : (null, default);
    }

    /// <summary>
    /// How presence is observed, as the supervisor on that node names it.
    /// </summary>
    /// <remarks>
    /// A word this build does not know is <see cref="PresenceDetection.Unknown"/> rather than a guess:
    /// an unrecognised mechanism is one whose reading nothing here can say the worth of, which is
    /// exactly what unknown means.
    /// </remarks>
    private static PresenceDetection MechanismOf(string? reported) => reported?.ToLowerInvariant() switch
    {
        "log" => PresenceDetection.Log,
        "rcon" => PresenceDetection.Rcon,
        "none" => PresenceDetection.None,
        _ => PresenceDetection.Unknown,
    };

    /// <summary>A byte count as a person reads one. Rounded for display and never re-parsed.</summary>
    private static string Bytes(long value) => value switch
    {
        >= 1L << 40 => $"{value / (double)(1L << 40):0.#} TiB",
        >= 1L << 30 => $"{value / (double)(1L << 30):0.#} GiB",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.#} MiB",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.#} KiB",
        _ => $"{value} B",
    };

    /// <summary>Where a server's files stand, as the node reports it. An unrecognised word is left
    /// unknown rather than mapped to the nearest thing it resembles.</summary>
    private static ServerLibraryState? LibraryStateOf(string? reported) => reported?.ToLowerInvariant() switch
    {
        "online" => ServerLibraryState.Online,
        "offline" => ServerLibraryState.Offline,
        "unregistered" => ServerLibraryState.Unregistered,
        _ => null,
    };

    /// <summary>Where a placed server's node answers. A roster read names the node the server was found
    /// on, and a node that has since left the roster answers nothing rather than being guessed at.</summary>
    private static ClusterNode Address(IReadOnlyList<ClusterNode> known, PlacedServer placed) =>
        known.FirstOrDefault(n => string.Equals(n.MemberId, placed.Node, StringComparison.Ordinal))
        ?? new ClusterNode(placed.Node, string.Empty);

    private static string Segment(string value) => Uri.EscapeDataString(value);
}
