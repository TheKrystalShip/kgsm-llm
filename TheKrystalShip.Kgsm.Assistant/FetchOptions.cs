namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Availability for the <c>fetch_url</c> tool — mirrors <see cref="SearchOptions"/>'s omit-when-disabled pattern
/// (compute-at-composition, offer-only-when-backed). The real adapter configuration (timeout, size
/// cap, redirect count, host allow/denylist, daily budget) lives in Infrastructure's own
/// <c>WebFetchOptions</c>; this type carries only the COMPUTED <see cref="Available"/> flag
/// Infrastructure derives from it, so <see cref="ServerAssistant"/>'s tool-offering decision stays
/// independent of the adapter's shape (the assistant project cannot reference Infrastructure).
/// </summary>
public sealed class FetchOptions
{
    /// <summary>Computed (not bound from config): true when a real <c>IWebFetch</c> adapter is
    /// enabled on this host (<c>WebFetch:Enabled</c>). Unlike <see cref="SearchOptions.Available"/>,
    /// fetching needs no API key — its own Enabled flag is the sole gate.</summary>
    public bool Available { get; set; }
}
