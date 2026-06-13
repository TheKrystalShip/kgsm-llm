namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Whether a <see cref="FleetStatusEntry"/> carries a real status reading or could
/// not be read. This is the toolbox-boundary mirror of kgsm-lib's typed-unknown
/// reading state: it keeps the assistant library decoupled from kgsm-lib (the host
/// maps its own reading type onto this) while preserving the one distinction that
/// must never collapse — "we measured it" vs "we couldn't read it." An instance
/// whose status could not be read must surface as <see cref="Unavailable"/>, never
/// as a fabricated "stopped."
/// </summary>
public enum FleetStatusAvailability
{
    /// <summary>The status was read; <see cref="FleetStatusEntry.Running"/> is meaningful.</summary>
    Read,

    /// <summary>The status could not be read; <see cref="FleetStatusEntry.Reason"/> explains why.</summary>
    Unavailable,
}

/// <summary>
/// One instance's brief status in a fleet read (the bulk counterpart to a
/// single-instance status). Deliberately minimal — the cheap, fan-out-free answer
/// to "which servers are running?" — so it stays a one-shot read instead of a
/// per-instance loop.
/// </summary>
/// <param name="Instance">The instance name.</param>
/// <param name="Availability">Whether the status could be read.</param>
/// <param name="Running">
/// Whether the instance is running. Null when <paramref name="Availability"/> is
/// <see cref="FleetStatusAvailability.Unavailable"/> — there is no measurement to
/// report, and a bare <c>false</c> would masquerade as "stopped."
/// </param>
/// <param name="Reason">
/// Why the status could not be read. Non-null only when
/// <paramref name="Availability"/> is <see cref="FleetStatusAvailability.Unavailable"/>.
/// </param>
public sealed record FleetStatusEntry(
    string Instance,
    FleetStatusAvailability Availability,
    bool? Running,
    string? Reason);
