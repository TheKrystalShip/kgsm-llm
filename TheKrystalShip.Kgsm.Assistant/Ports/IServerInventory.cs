namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Read-only view of the live kgsm inventory, served from the host's cache. Feeds
/// the system-prompt injection and the dispatcher's name resolution. The host owns
/// the actual cache + refresh strategy; the assistant only consumes these reads.
/// </summary>
public interface IServerInventory
{
    /// <summary>Installed instances as a map of instance id → game (blueprint) type.</summary>
    Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installed instances as a map of instance id → the label a person calls it by.
    /// </summary>
    /// <remarks>
    /// A server has two names. Its <b>id</b> is generated at install, never changes, and is what every
    /// tool argument, path and event carries; its <b>display name</b> is free text somebody chose and
    /// changes whenever they like. Both are shown wherever the model reads a list of servers, so
    /// "restart My Factorio" can be turned into the id the tool takes — and a value never leaves this
    /// process as anything but the id.
    /// <para>
    /// Never blank: a server with no label of its own reads as its id, so a caller can print the value
    /// without checking it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetInstanceLabelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Names of the installable blueprints (game types).</summary>
    Task<IReadOnlyCollection<string>> GetBlueprintNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The installable blueprints with the name a person calls each game by. Read this wherever the
    /// catalog is shown to somebody; <see cref="GetBlueprintNamesAsync"/> is for the places that need
    /// the engine's identifier and nothing else.
    /// </summary>
    Task<IReadOnlyList<BlueprintSummary>> GetBlueprintCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One blueprint's detail, or <see langword="null"/> when no such blueprint exists. Null is
    /// "unknown game type", not "the read failed" — implementations must not throw.
    /// </summary>
    Task<BlueprintDetail?> GetBlueprintDetailAsync(
        string blueprintName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which machine each server is on, when that is a thing worth saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty when there is one machine and nothing to attribute — an inventory with a single source
    /// answers for the only host there is, and naming it on every line would be noise. A fleet
    /// assembled from several says which, because the alternative is a reader with no way to tell and
    /// a model that fills the gap in: asked which machine a server was on, it answered "this host"
    /// for every one of them, including the one on another machine.
    /// </para>
    /// <para>
    /// It does not change how a server is addressed. An id means the same thing everywhere and every
    /// tool still takes exactly that; this is a fact about where the server is, not a second way to
    /// name it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetInstanceHostsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What these lists could not be read from, each named with why, so an answer built on them can
    /// say what is missing from it.
    /// </summary>
    /// <remarks>
    /// Empty is the ordinary answer and the only one an inventory with a single source can give: it
    /// either read that source or it did not, and a list that came back empty is visibly empty. An
    /// inventory assembled from several is the case this exists for — a list short by one machine
    /// looks exactly like a smaller fleet, and nothing in it says which.
    /// </remarks>
    Task<IReadOnlyList<string>> GetUnreachedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One installable game type: the engine's identifier and the name a person calls the game by.
/// <para>
/// The two are different things — <c>lotrrtm</c> is what kgsm installs, "The Lord of the Rings:
/// Return to Moria" is what somebody asked for — and a surface that speaks its answers has to say
/// the second. <see cref="Label"/> is the one to render; a blueprint that declares no display name
/// falls back to its identifier, because a slug read aloud is still better than nothing.
/// </para>
/// </summary>
public sealed record BlueprintSummary(string Name, string? DisplayName)
{
    /// <summary>What to show a person: the display name when the blueprint declares one.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>
/// What a game type is and needs, for answering "what can I run?" and "what does this game want?"
/// before an install is proposed. Every capacity figure is nullable because a blueprint may simply
/// not declare it — an absent figure is unknown, never a zero.
/// <para>
/// <see cref="ModerationVerbs"/> lists only the moderation actions this game's server actually
/// supports (a blueprint declares each command, and an undeclared one is unsupported). It is what
/// stops the assistant offering to ban somebody on a game that cannot.
/// </para>
/// </summary>
public sealed record BlueprintDetail(
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Ports,
    string Kind,
    bool SteamAccountRequired,
    int? MaxPlayers,
    int? MinRamMb,
    int? RecommendedRamMb,
    int? BaseDiskMb,
    IReadOnlyList<string> ModerationVerbs);
