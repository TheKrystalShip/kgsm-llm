namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The id a blueprint's test-install carries, and how a leftover one is recognised.
/// </summary>
/// <remarks>
/// A probe is installed under an id this code chooses rather than one the engine generates, because
/// everything after the install — reading its boot log, walking its files, tearing it down —
/// addresses it by that id.
/// </remarks>
public static class BlueprintProbeNaming
{
    /// <summary>
    /// The prefix every probe id starts with. No real user-requested instance may ever collide with
    /// it: an id is generated from a blueprint's own name, and this is not one the engine would mint.
    /// </summary>
    /// <remarks>
    /// It begins with a letter because an instance id must (<c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>)
    /// — the engine refuses one that does not, and a refused probe install is a blueprint that can
    /// never be verified.
    /// </remarks>
    public const string Prefix = "bpprobe__";

    /// <summary>
    /// The other spelling a probe on disk may carry. Recognised by <see cref="IsProbe"/> and never
    /// minted: the sweep's job is clearing leftovers, and one it does not recognise is one nothing
    /// ever removes.
    /// </summary>
    private const string UnderscorePrefix = "__bp_probe_";

    /// <summary>Builds the probe id for a blueprint slug (already a safe <c>[a-z0-9_-]</c> name).</summary>
    public static string ForSlug(string slug) => $"{Prefix}{slug}__";

    /// <summary>True when <paramref name="instanceName"/> is a probe — what the startup sweep
    /// filters on before uninstalling.</summary>
    public static bool IsProbe(string instanceName) =>
        !string.IsNullOrEmpty(instanceName)
        && (instanceName.StartsWith(Prefix, StringComparison.Ordinal)
            || instanceName.StartsWith(UnderscorePrefix, StringComparison.Ordinal));
}
