using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IServerInventory"/> port from KGSM.Lib's
/// <see cref="IInstanceService"/> / <see cref="IBlueprintService"/>, with a TTL cache,
/// dirty-flag invalidation, and last-known-good on refresh failure — the same shape as the
/// bot's <c>KgsmStateCache</c>. Hosts invalidate via <see cref="IInventoryInvalidation"/>: the
/// HTTP service from its <c>/events</c> webhook, the CLI after a confirmed mutating action.
/// <para>
/// Note it depends on the instance/blueprint services, NOT <c>IKgsmClient</c>, so the host
/// never constructs KGSM.Lib's socket event listener (see <see cref="KgsmServerOperations"/>).
/// </para>
/// </summary>
internal sealed class KgsmServerInventory : IServerInventory, IInventoryInvalidation
{
    private static readonly IReadOnlyDictionary<string, string> EmptyInstances = new Dictionary<string, string>();
    private static readonly IReadOnlyList<BlueprintSummary> EmptyBlueprints = Array.Empty<BlueprintSummary>();

    private readonly IInstanceService _instances;
    private readonly IBlueprintService _blueprints;
    private readonly InventoryCacheOptions _options;
    private readonly ILogger<KgsmServerInventory> _logger;

    private readonly SemaphoreSlim _instancesLock = new(1, 1);
    private readonly SemaphoreSlim _blueprintsLock = new(1, 1);

    private IReadOnlyDictionary<string, string>? _instancesCache;
    private IReadOnlyDictionary<string, string>? _labelsCache;
    private DateTime _instancesFetchedUtc = DateTime.MinValue;
    private volatile bool _instancesDirty = true;

    private IReadOnlyList<BlueprintSummary>? _blueprintsCache;
    private DateTime _blueprintsFetchedUtc = DateTime.MinValue;
    private volatile bool _blueprintsDirty = true;

    public KgsmServerInventory(
        IInstanceService instances,
        IBlueprintService blueprints,
        IOptions<InventoryCacheOptions> options,
        ILogger<KgsmServerInventory> logger)
    {
        _instances = instances;
        _blueprints = blueprints;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Marks both caches stale so the next read refreshes. Called by the webhook receiver.</summary>
    public void Invalidate()
    {
        _instancesDirty = true;
        _blueprintsDirty = true;
    }

    public void InvalidateInstances() => _instancesDirty = true;

    public void InvalidateBlueprints() => _blueprintsDirty = true;

    public async Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        await RefreshInstancesAsync(cancellationToken);
        return _instancesCache ?? EmptyInstances;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetInstanceLabelsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshInstancesAsync(cancellationToken);
        return _labelsCache ?? EmptyInstances;
    }

    /// <summary>
    /// Brings both instance views up to date from one engine read.
    /// </summary>
    /// <remarks>
    /// The games and the labels come off the same roster entry, so reading them separately would pay
    /// twice for one call and could answer two questions about two different moments — a server
    /// renamed between them would be listed under a game it is not running.
    /// </remarks>
    private async Task RefreshInstancesAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(_instancesCache, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
            return;

        await _instancesLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(_instancesCache, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
                return;

            try
            {
                var all = await Task.Run(() => _instances.GetAll(), cancellationToken);
                // The engine's record carries the blueprint's bare name, which is the spelling every
                // catalog lookup and every sentence the model reads is keyed on.
                _instancesCache = all.ToDictionary(kv => kv.Key, kv => kv.Value.Blueprint);
                // The label is never blank — kgsm-lib answers with the id for an instance carrying no
                // label of its own — so a reader can print it without deciding what an empty one means.
                _labelsCache = all.ToDictionary(kv => kv.Key, kv => kv.Value.DisplayName);
                _instancesFetchedUtc = DateTime.UtcNow;
                _instancesDirty = false;
                _logger.LogDebug("Instance cache refreshed ({Count} instances)", _instancesCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Instance cache refresh failed; serving {State}",
                    _instancesCache is null ? "empty" : "stale snapshot");
            }
        }
        finally
        {
            _instancesLock.Release();
        }
    }

    public async Task<IReadOnlyCollection<string>> GetBlueprintNamesAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await GetBlueprintCatalogAsync(cancellationToken);
        return catalog.Select(b => b.Name).ToArray();
    }

    /// <summary>
    /// The catalog read both blueprint views are served from. <c>ListDetailed</c> already carries each
    /// blueprint's metadata, so the display name costs nothing beyond keeping it — the alternative,
    /// a detail read per game, would be one engine invocation per blueprint on every refresh.
    /// </summary>
    public async Task<IReadOnlyList<BlueprintSummary>> GetBlueprintCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh(_blueprintsCache, _blueprintsFetchedUtc, _blueprintsDirty, _options.BlueprintsTtlSeconds))
            return _blueprintsCache!;

        await _blueprintsLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(_blueprintsCache, _blueprintsFetchedUtc, _blueprintsDirty, _options.BlueprintsTtlSeconds))
                return _blueprintsCache!;

            try
            {
                var all = await Task.Run(() => _blueprints.ListDetailed(), cancellationToken);
                _blueprintsCache = all
                    .Select(kv => new BlueprintSummary(kv.Key, NullIfBlank(kv.Value?.Metadata?.DisplayName)))
                    .ToArray();
                _blueprintsFetchedUtc = DateTime.UtcNow;
                _blueprintsDirty = false;
                _logger.LogDebug("Blueprint cache refreshed ({Count} blueprints)", _blueprintsCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Blueprint cache refresh failed; serving {State}",
                    _blueprintsCache is null ? "empty" : "stale snapshot");
            }

            return _blueprintsCache ?? EmptyBlueprints;
        }
        finally
        {
            _blueprintsLock.Release();
        }
    }

    private static bool IsFresh<T>(T? cache, DateTime fetchedUtc, bool dirty, int ttlSeconds)
        where T : class =>
        cache is not null && !dirty && DateTime.UtcNow - fetchedUtc < TimeSpan.FromSeconds(ttlSeconds);

    /// <summary>
    /// Reads one blueprint's detail straight from the engine — uncached, unlike the name list: a
    /// detail read happens once per question, so the cache would only add staleness.
    /// </summary>
    public async Task<BlueprintDetail?> GetBlueprintDetailAsync(
        string blueprintName, CancellationToken cancellationToken = default)
    {
        try
        {
            var bp = await Task.Run(() => _blueprints.GetInfo(blueprintName), cancellationToken);
            if (bp is null)
                return null;

            // Only the moderation commands the blueprint actually declares. An undeclared command is
            // one the game cannot do, which is what keeps the assistant from offering it.
            var moderation = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(bp.KickCommand)) moderation.Add("kick");
            if (!string.IsNullOrWhiteSpace(bp.BanCommand)) moderation.Add("ban");
            if (!string.IsNullOrWhiteSpace(bp.UnbanCommand)) moderation.Add("unban");

            return new BlueprintDetail(
                Name: bp.Name,
                DisplayName: NullIfBlank(bp.Metadata?.DisplayName),
                Description: NullIfBlank(bp.Metadata?.Description),
                Ports: bp.Ports.Select(p => p.ToString()).ToArray(),
                Kind: bp.BlueprintType.ToString().ToLowerInvariant(),
                SteamAccountRequired: bp.IsSteamAccountRequired,
                MaxPlayers: bp.Metadata?.MaxPlayers,
                MinRamMb: bp.Metadata?.MinRamMb,
                RecommendedRamMb: bp.Metadata?.RecommendedRamMb,
                BaseDiskMb: bp.Metadata?.BaseDiskMb,
                ModerationVerbs: moderation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blueprint detail read failed for {Blueprint}", blueprintName);
            return null;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
