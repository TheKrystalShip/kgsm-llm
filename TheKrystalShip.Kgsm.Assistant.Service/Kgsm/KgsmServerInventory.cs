using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Service.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IServerInventory"/> port from KGSM.Lib's
/// <see cref="IInstanceService"/> / <see cref="IBlueprintService"/>, with a TTL cache,
/// dirty-flag invalidation, and last-known-good on refresh failure — the same shape as the
/// bot's <c>KgsmStateCache</c>. The bot invalidates from socket events; this service
/// invalidates from the <c>/events</c> webhook via <see cref="Invalidate"/>.
/// <para>
/// Note it depends on the instance/blueprint services, NOT <c>IKgsmClient</c>, so the service
/// never constructs KGSM.Lib's socket event listener (see <see cref="KgsmServerOperations"/>).
/// </para>
/// </summary>
internal sealed class KgsmServerInventory : IServerInventory
{
    private static readonly IReadOnlyDictionary<string, string> EmptyInstances = new Dictionary<string, string>();
    private static readonly IReadOnlyCollection<string> EmptyBlueprints = Array.Empty<string>();

    private readonly IInstanceService _instances;
    private readonly IBlueprintService _blueprints;
    private readonly InventoryCacheOptions _options;
    private readonly ILogger<KgsmServerInventory> _logger;

    private readonly SemaphoreSlim _instancesLock = new(1, 1);
    private readonly SemaphoreSlim _blueprintsLock = new(1, 1);

    private IReadOnlyDictionary<string, string>? _instancesCache;
    private DateTime _instancesFetchedUtc = DateTime.MinValue;
    private volatile bool _instancesDirty = true;

    private IReadOnlyCollection<string>? _blueprintsCache;
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

    public async Task<IReadOnlyDictionary<string, string>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh(_instancesCache, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
            return _instancesCache!;

        await _instancesLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(_instancesCache, _instancesFetchedUtc, _instancesDirty, _options.InstancesTtlSeconds))
                return _instancesCache!;

            try
            {
                var all = await Task.Run(() => _instances.GetAll(), cancellationToken);
                _instancesCache = all.ToDictionary(kv => kv.Key, kv => kv.Value.Blueprint ?? string.Empty);
                _instancesFetchedUtc = DateTime.UtcNow;
                _instancesDirty = false;
                _logger.LogDebug("Instance cache refreshed ({Count} instances)", _instancesCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Instance cache refresh failed; serving {State}",
                    _instancesCache is null ? "empty" : "stale snapshot");
            }

            return _instancesCache ?? EmptyInstances;
        }
        finally
        {
            _instancesLock.Release();
        }
    }

    public async Task<IReadOnlyCollection<string>> GetBlueprintNamesAsync(CancellationToken cancellationToken = default)
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
                _blueprintsCache = all.Keys.ToArray();
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
}
