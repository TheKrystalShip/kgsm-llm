using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Status;

/// <summary>
/// One server's run state in a fleet card. <see cref="Unknown"/> is the toolbox-boundary mirror of
/// <see cref="FleetStatusAvailability.Unavailable"/> — a status that could NOT be read. It must
/// never collapse into <see cref="Stopped"/> (a bare "not running" would masquerade as a measured
/// fact); the same never-fabricate doctrine as the health card's stopped≠failure rule.
/// </summary>
public enum ServerRunState
{
    /// <summary>Measured running.</summary>
    Running,

    /// <summary>Measured stopped (idle) — a stateless engine has no desired-state, so this is info, not a failure.</summary>
    Stopped,

    /// <summary>Status could not be read — honestly unknown, never a fabricated "stopped".</summary>
    Unknown,
}

/// <summary>
/// One server's entry in the <see cref="FleetStatusData"/> card. <see cref="Reason"/> is non-null
/// only when <see cref="State"/> is <see cref="ServerRunState.Unknown"/> (why the read failed).
/// </summary>
/// <param name="Instance">The instance name.</param>
/// <param name="State">Running / stopped / unknown (the never-collapsed read state).</param>
/// <param name="Severity">The rendered weight: running→Success, stopped→Info, unknown→Warn.</param>
/// <param name="Reason">Why the status couldn't be read; null unless <see cref="State"/> is Unknown.</param>
public sealed record FleetServerStatus(
    string Instance, ServerRunState State, Severity Severity, string? Reason);

/// <summary>
/// The structured card payload for the fleet mode of <c>server_info</c> (no instance_name → a
/// one-shot summary of every server). The <c>D</c> in <see cref="ToolResult{TData}"/>. Counts are
/// honest: an unreadable instance lands in <see cref="Unavailable"/>, never inflating
/// <see cref="Stopped"/>.
/// </summary>
/// <param name="Servers">Every instance, in the order kgsm returned them.</param>
/// <param name="Total">Total instances.</param>
/// <param name="Running">How many are measured running.</param>
/// <param name="Stopped">How many are measured stopped.</param>
/// <param name="Unavailable">How many could not be read (status unknown).</param>
public sealed record FleetStatusData(
    IReadOnlyList<FleetServerStatus> Servers,
    int Total,
    int Running,
    int Stopped,
    int Unavailable);

/// <summary>
/// The deterministic <c>server_info</c> fleet-mode card builder: projects the
/// neutral <see cref="FleetStatusEntry"/> list into a ranked <see cref="FleetStatusData"/> card plus
/// a model-grounding <see cref="ToolResult{TData}.Summary"/>. Pure and I/O-free (the fetch happens in
/// the port impl), so it's the single home for the projection and is unit-testable without mocks.
/// <para>
/// The <see cref="ToolResult{TData}.Summary"/> is kept byte-identical to the dispatcher's prior
/// inline string so the model's grounding text does not change. The <see cref="ToolResult{TData}.Subject"/>
/// is host-scoped (the card is about every server on this host, not one instance); the host id uses
/// the <c>architecture.html</c> self-reference convention <c>"primary"</c> — informational, since the
/// per-host Control Panel already knows which host it's connected to.
/// </para>
/// </summary>
public static class FleetStatusCard
{
    /// <summary>The architecture.html host self-reference (denormalized / derived-if-absent) — see the type remarks.</summary>
    private const string HostSubjectId = "primary";

    /// <summary>
    /// Builds the fleet card from a successful read. A read FAILURE never reaches here — the
    /// dispatcher returns a summary-only error string for that. An empty list is a real measured
    /// result (zero servers) and yields an empty card, not a dropped one.
    /// </summary>
    /// <param name="entries">The measured run state of every instance.</param>
    /// <param name="presence">
    /// Who is connected, for the same instances. Optional: it comes from a different authority and a
    /// supervisor that cannot answer costs the roster clause and nothing else. It rides along because
    /// it is ONE call for the whole fleet — "what's running" is almost always followed by "is anyone
    /// on it", and answering both here is free where answering it per instance would be N round
    /// trips.
    /// </param>
    public static ToolResult<FleetStatusData> Build(
        IReadOnlyList<FleetStatusEntry> entries, PresenceReading? presence = null)
    {
        var servers = entries.Select(Map).ToArray();

        var data = new FleetStatusData(
            servers,
            Total: servers.Length,
            Running: servers.Count(s => s.State == ServerRunState.Running),
            Stopped: servers.Count(s => s.State == ServerRunState.Stopped),
            Unavailable: servers.Count(s => s.State == ServerRunState.Unknown));

        return new ToolResult<FleetStatusData>(
            Tool: ResultCardKinds.Status,
            Confidence: Confidence.Confirmed, // a deterministic read of measured facts (incl. honest "couldn't read")
            Subject: new ResultRef(ResourceKind.Host, HostSubjectId),
            Summary: BuildSummary(entries, presence),
            Data: data);
    }

    private static FleetServerStatus Map(FleetStatusEntry e) =>
        e.Availability == FleetStatusAvailability.Read
            ? (e.Running == true
                ? new FleetServerStatus(e.Instance, ServerRunState.Running, Severity.Success, null)
                : new FleetServerStatus(e.Instance, ServerRunState.Stopped, Severity.Info, null))
            // Unavailable: honestly unknown — NEVER collapsed to Stopped. Carry the reason.
            : new FleetServerStatus(e.Instance, ServerRunState.Unknown, Severity.Warn, e.Reason);

    /// <summary>
    /// The model's grounding text. An unreadable instance is reported as "status unavailable
    /// (reason)", never collapsed to "stopped", and a running instance carries who is on it when
    /// the supervisor could say.
    /// </summary>
    private static string BuildSummary(IReadOnlyList<FleetStatusEntry> entries, PresenceReading? presence)
    {
        if (entries.Count == 0)
            return "There are no installed server instances.";

        var lines = entries.Select(e => e.Availability == FleetStatusAvailability.Read
            ? $"- {e.Instance}: {(e.Running == true ? "running" : "stopped")}{Roster(e, presence)}"
            : $"- {e.Instance}: status unavailable ({e.Reason ?? "unknown reason"})");

        return $"Status of all {entries.Count} server(s):\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Who is connected to a running instance. Silent for a stopped one (nobody is on a server that
    /// is not up, and saying so for every stopped server is noise), and silent when presence was not
    /// read at all — an absent clause is honest, where "0 connected" would be a measurement nobody
    /// made. A game whose players cannot be observed says exactly that.
    /// </summary>
    private static string Roster(FleetStatusEntry entry, PresenceReading? presence)
    {
        if (entry.Running != true || presence is null || presence.State == FactsState.Unavailable)
            return string.Empty;

        var row = presence.Instances.FirstOrDefault(
            i => string.Equals(i.Instance, entry.Instance, StringComparison.OrdinalIgnoreCase));

        if (row is null)
            return string.Empty;

        if (!row.IsMeasured)
            return ", this game doesn't report who is connected";

        return row.Players.Count == 0
            ? ", nobody connected"
            : $", {row.Players.Count} connected ("
              + string.Join(", ", row.Players.Select(p => p.Name ?? p.Id ?? "unnamed")) + ")";
    }
}
