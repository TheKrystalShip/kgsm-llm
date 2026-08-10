namespace TheKrystalShip.Kgsm.Assistant.Audit;

/// <summary>
/// Whether an engine-event read could reach the journal. Mirrors
/// <see cref="Metrics.PerformanceState"/>'s honest split: <see cref="Available"/> means the journal
/// was read (the event list may still be empty — a real "nothing happened", never an error);
/// <see cref="JournalUnavailable"/> means it could not be — an honest "couldn't read", explicitly
/// NOT evidence that nothing happened.
/// </summary>
public enum AuditReadState
{
    /// <summary>The journal was read; <see cref="AuditData.Events"/> is the real (possibly empty) result.</summary>
    Available,

    /// <summary>
    /// The engine's event journal is absent or unreadable, so history cannot be answered for. Distinct
    /// from an empty <see cref="Available"/> result: one says "nothing happened", this says "I cannot
    /// see", and reporting the second as the first would state silence as fact.
    /// </summary>
    JournalUnavailable,
}

/// <summary>
/// One raw engine event, as the engine's journal holds it — a KGSM lifecycle event with the
/// enrichment trio, relayed verbatim. <see cref="Instance"/> is <see langword="null"/>
/// for a host/global event; <see cref="Actor"/>/<see cref="Origin"/> are <see langword="null"/> when
/// the emitter supplied no enrichment (a bare CLI call) — never fabricated, and never defaulted to a
/// placeholder like "system". <see cref="Type"/> is the raw kgsm event name (e.g.
/// <c>instance_started</c>) — no domain shaping; that stays a read-time concern of the composer that
/// builds the model-grounding summary.
/// <see cref="Blueprint"/> names what a blueprint event acts on (<c>blueprint_created</c> and its
/// siblings) — the only name such an event has, since it carries no <see cref="Instance"/>;
/// <see langword="null"/> everywhere else.
/// </summary>
public sealed record AuditEventRow(
    string Id,
    DateTimeOffset Ts,
    string Type,
    string? Instance,
    string? Actor,
    string? Origin,
    string? Blueprint = null);

/// <summary>
/// The shared structured card payload for both scopes of <c>events</c>
///: the two tools read the same engine-event source and differ only in filtering
/// and framing, so they share one card shape and are told apart by the producing
/// <see cref="Envelope.ToolResult{TData}.Tool"/> (its <c>Section</c> is <c>"all"</c> vs <c>"changes"</c>).
/// <see cref="Events"/> is already ts-DESC (most-recent-first) and, for the timeline tool, already
/// filtered to the state-changing subset (see <c>ChangeTimelineReport</c>) — the surface renders
/// whatever it's given, no further filtering. An honest empty list ("no events recorded" / "no
/// changes recorded") is a real result, not an error.
/// </summary>
/// <param name="Instance">The instance the read was scoped to; <see langword="null"/> = fleet-wide (every instance).</param>
/// <param name="Window">The requested window/range, normalized (e.g. <c>24h</c>) — an unrecognized
/// request is honestly substituted with the tool's default, never rejected.</param>
/// <param name="State">Whether the journal could be read at all.</param>
/// <param name="Events">The rows, most-recent-first; empty is a real "nothing recorded" result.</param>
public sealed record AuditData(
    string? Instance,
    string Window,
    AuditReadState State,
    IReadOnlyList<AuditEventRow> Events);
