namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Read-only view of the live kgsm inventory, served from the host's cache. Feeds
/// the system-prompt injection and the dispatcher's name resolution. The host owns
/// the actual cache + refresh strategy; the assistant only consumes these reads.
/// </summary>
public interface IServerInventory
{
    /// <summary>Installed instances as a map of instance name → game (blueprint) type.</summary>
    Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default);

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
