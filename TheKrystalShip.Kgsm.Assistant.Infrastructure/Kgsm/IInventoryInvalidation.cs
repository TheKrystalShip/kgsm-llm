namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// The one cache capability a host needs from the inventory adapter: mark it stale so the next
/// read refreshes. The concrete <c>KgsmServerInventory</c> stays <see langword="internal"/> to this
/// library (its read surface is the assistant's <c>IServerInventory</c> port); this small public
/// seam lets a host force a refresh without a friend-assembly dependency on the internal type.
/// <para>
/// Registered against the same singleton instance as <c>IServerInventory</c>, so invalidating here
/// and reading there hit one cache. Several callers, each an independent freshness path: the HTTP
/// service's <c>KgsmEventListener</c> (the engine's journal, which is how a change made by any other
/// surface arrives) and its <c>/events</c> webhook, the kgsm chokepoint after a mutation this process
/// performed itself, and the blueprint authoring lane after its own writes — which is the CLI's only
/// path, since it reads no journal and has no webhook.
/// </para>
/// <para>
/// The instance roster and the blueprint catalog are cached separately and change for separate
/// reasons, so each has its own method: a server installed does not change what games exist, and a
/// blueprint edited does not change what is installed. Dropping both would cost the untouched half a
/// <c>kgsm</c> subprocess on its next read for nothing.
/// </para>
/// </summary>
public interface IInventoryInvalidation
{
    /// <summary>Marks the instance + blueprint caches stale so the next read refreshes from kgsm.</summary>
    void Invalidate();

    /// <summary>Marks the instance roster stale, leaving the blueprint catalog as it stands.</summary>
    void InvalidateInstances();

    /// <summary>Marks the blueprint catalog stale, leaving the instance roster as it stands.</summary>
    void InvalidateBlueprints();
}
