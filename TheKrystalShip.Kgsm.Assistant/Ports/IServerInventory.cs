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
}
