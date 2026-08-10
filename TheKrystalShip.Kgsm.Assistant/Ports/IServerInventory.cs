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
    /// One blueprint's detail, or <see langword="null"/> when no such blueprint exists. Null is
    /// "unknown game type", not "the read failed" — implementations must not throw.
    /// </summary>
    Task<BlueprintDetail?> GetBlueprintDetailAsync(
        string blueprintName, CancellationToken cancellationToken = default);
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
