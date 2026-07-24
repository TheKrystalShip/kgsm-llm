namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The reserved instance-name scheme for a <c>create_blueprint</c> test install — a disposable,
/// never-user-facing probe torn down before the tool returns (guaranteed teardown, plus a startup
/// sweep for anything a crash left behind). Centralised here so the aggregator (that creates a probe)
/// and the sweep (that looks for orphans) can never drift on the prefix.
/// </summary>
public static class BlueprintProbeNaming
{
    /// <summary>The prefix every probe instance name starts with. No real user-requested instance may
    /// ever collide with this — kgsm instance names are user-chosen slugs, and this prefix is not one a
    /// person or the model's normal naming would produce.</summary>
    public const string Prefix = "__bp_probe_";

    /// <summary>Builds the probe instance name for a blueprint slug (already a safe <c>[a-z0-9_-]</c> name).</summary>
    public static string ForSlug(string slug) => $"{Prefix}{slug}__";

    /// <summary>True when <paramref name="instanceName"/> is a probe name — what the startup sweep
    /// filters on before uninstalling.</summary>
    public static bool IsProbe(string instanceName) =>
        !string.IsNullOrEmpty(instanceName) && instanceName.StartsWith(Prefix, StringComparison.Ordinal);
}
