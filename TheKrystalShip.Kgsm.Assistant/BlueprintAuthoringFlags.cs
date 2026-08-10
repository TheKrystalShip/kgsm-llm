namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Availability for the <c>create_blueprint</c> tool — mirrors <see cref="FetchOptions"/>'s omit-when-disabled pattern
/// (compute-at-composition, offer-only-when-backed). The real pipeline configuration (enabled flag,
/// stash directory, verify timeouts, self-repair bound) lives in Infrastructure's own
/// <c>BlueprintAuthoringOptions</c>; this type carries only the COMPUTED <see cref="Available"/> flag
/// Infrastructure derives from it, so <see cref="ServerAssistant"/>'s tool-offering decision stays
/// independent of the pipeline's shape (the assistant project cannot reference Infrastructure).
/// </summary>
public sealed class BlueprintAuthoringFlags
{
    /// <summary>Computed (not bound from config): true when the real pipeline is enabled on this host
    /// (<c>BlueprintAuthoring:Enabled</c>). False everywhere by default.</summary>
    public bool Available { get; set; }
}
