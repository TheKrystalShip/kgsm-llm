namespace TheKrystalShip.Kgsm.Assistant.Audit;

/// <summary>
/// Whether an engine-event read actually reached the monitor. Mirrors
/// <see cref="Metrics.PerformanceState"/>'s honest split: <see cref="Available"/> means the monitor
/// answered (the event list may still be empty — a real "nothing happened", never an error);
/// <see cref="MonitorUnavailable"/> means the read failed (unreachable/unparseable monitor) — an
/// honest "couldn't read", explicitly NOT evidence that nothing happened.
/// </summary>
public enum AuditReadState
{
    /// <summary>The monitor answered; <see cref="AuditData.Events"/> is the real (possibly empty) result.</summary>
    Available,

    /// <summary>The monitor could not be reached or its response could not be read/parsed.</summary>
    MonitorUnavailable,
}

/// <summary>
/// One raw engine event, as the monitor's <c>GET /events</c> serves it (Phase B) — a KGSM lifecycle
/// event with the enrichment trio, relayed verbatim. <see cref="Instance"/> is <see langword="null"/>
/// for a host/global event; <see cref="Actor"/>/<see cref="Origin"/> are <see langword="null"/> when
/// the emitter supplied no enrichment (a bare CLI call) — never fabricated, and never defaulted to a
/// placeholder like "system". <see cref="Type"/> is the raw kgsm event name (e.g.
/// <c>instance_started</c>) — no domain shaping; that stays a read-time concern of the composer that
/// builds the model-grounding summary.
/// </summary>
public sealed record AuditEventRow(
    string Id,
    DateTimeOffset Ts,
    string Type,
    string? Instance,
    string? Actor,
    string? Origin);

/// <summary>
/// The shared structured card payload for both <c>get_audit_log</c> and <c>get_change_timeline</c>
/// (toolbox-plan §4.1): the two tools read the same engine-event source and differ only in filtering
/// and framing, so they share one card shape and are told apart by the producing
/// <see cref="Envelope.ToolResult{TData}.Tool"/> (<c>"get_audit_log"</c> vs <c>"get_change_timeline"</c>).
/// <see cref="Events"/> is already ts-DESC (most-recent-first) and, for the timeline tool, already
/// filtered to the state-changing subset (see <c>ChangeTimelineReport</c>) — the surface renders
/// whatever it's given, no further filtering. An honest empty list ("no events recorded" / "no
/// changes recorded") is a real result, not an error.
/// </summary>
/// <param name="Instance">The instance the read was scoped to; <see langword="null"/> = fleet-wide (every instance).</param>
/// <param name="Window">The requested window/range, normalized (e.g. <c>24h</c>) — an unrecognized
/// request is honestly substituted with the tool's default, never rejected.</param>
/// <param name="State">Whether the monitor could be read at all.</param>
/// <param name="Events">The rows, most-recent-first; empty is a real "nothing recorded" result.</param>
public sealed record AuditData(
    string? Instance,
    string Window,
    AuditReadState State,
    IReadOnlyList<AuditEventRow> Events);
