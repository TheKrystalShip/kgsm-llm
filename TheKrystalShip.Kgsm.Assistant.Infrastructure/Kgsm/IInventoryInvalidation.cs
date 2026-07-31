namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// The one cache capability a host needs from the inventory adapter: mark it stale so the next
/// read refreshes. The concrete <c>KgsmServerInventory</c> stays <see langword="internal"/> to this
/// library (its read surface is the assistant's <c>IServerInventory</c> port); this small public
/// seam lets a host force a refresh without a friend-assembly dependency on the internal type.
/// <para>
/// Registered against the same singleton instance as <c>IServerInventory</c>, so invalidating here
/// and reading there hit one cache. Three callers, each an independent freshness path: the HTTP
/// service's <c>KgsmEventListener</c> (the engine's events over its own socket) and its
/// <c>/events</c> webhook, and the blueprint authoring lane after its own writes — which is the
/// CLI's only path, since it binds no socket and has no webhook.
/// </para>
/// </summary>
public interface IInventoryInvalidation
{
    /// <summary>Marks the instance + blueprint caches stale so the next read refreshes from kgsm.</summary>
    void Invalidate();
}
