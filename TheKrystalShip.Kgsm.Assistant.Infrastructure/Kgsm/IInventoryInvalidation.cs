namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// The one cache capability a host needs from the inventory adapter: mark it stale so the next
/// read refreshes. The concrete <c>KgsmServerInventory</c> stays <see langword="internal"/> to this
/// library (its read surface is the assistant's <c>IServerInventory</c> port); this small public
/// seam lets a host force a refresh without a friend-assembly dependency on the internal type.
/// <para>
/// Registered against the same singleton instance as <c>IServerInventory</c>, so invalidating here
/// and reading there hit one cache. The HTTP service calls it from its <c>/events</c> webhook; the
/// CLI calls it after a confirmed mutating action (it has no webhook — TTL + this is its freshness).
/// </para>
/// </summary>
public interface IInventoryInvalidation
{
    /// <summary>Marks the instance + blueprint caches stale so the next read refreshes from kgsm.</summary>
    void Invalidate();
}
