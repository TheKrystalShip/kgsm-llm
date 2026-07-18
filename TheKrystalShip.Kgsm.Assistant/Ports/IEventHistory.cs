using TheKrystalShip.Kgsm.Assistant.Audit;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// A read of raw engine events from the monitor's event store (Phase B — <c>GET /events</c>), as the
/// <see cref="IEventHistory"/> port returns it. <see cref="AuditData.State"/> carries the outcome
/// (<see cref="AuditReadState.Available"/> with rows — possibly none — or
/// <see cref="AuditReadState.MonitorUnavailable"/> with an empty list); failure is data, never an
/// exception. <see cref="Events"/> is ts-DESC (most-recent-first), unfiltered by event type — the
/// caller (the <c>get_audit_log</c> / <c>get_change_timeline</c> composers) decides what subset it needs.
/// </summary>
public sealed record EventHistoryReading(AuditReadState State, IReadOnlyList<AuditEventRow> Events);

/// <summary>
/// Reads KGSM <em>engine</em> event history straight from the kgsm-monitor (the single source of
/// truth for engine events — plan §9/Phase B), never via kgsm-api: the assistant is a leaf and reads
/// the monitor directly, exactly like <see cref="IServerMetrics"/>. Backs the model-facing
/// <c>get_audit_log</c> and <c>get_change_timeline</c> tools. Additive and fails closed: with no
/// monitor reachable a read returns <see cref="AuditReadState.MonitorUnavailable"/>, so the assistant
/// composes and boots standalone. Implementations MUST NOT throw — every failure maps to
/// <see cref="AuditReadState.MonitorUnavailable"/>.
/// </summary>
public interface IEventHistory
{
    /// <summary>
    /// Reads recent engine events, most-recent-first.
    /// </summary>
    /// <param name="instance">Scope to one instance's events; <see langword="null"/>/blank = every instance (fleet-wide).</param>
    /// <param name="sinceMs">Only events at or after this unix-ms timestamp; <see langword="null"/> = no lower bound.</param>
    /// <param name="limit">The maximum number of rows to return (the monitor caps this server-side).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IEventHistory"/> for hosts that have not wired a metrics monitor: every read
/// fails closed as <see cref="AuditReadState.MonitorUnavailable"/>, so embedding the assistant library
/// never breaks DI just because the monitor isn't configured — mirrors
/// <see cref="UnavailableServerMetrics"/>. <c>AddKgsmAssistant</c> registers this with
/// <c>TryAddSingleton</c>; a host that wires the monitor registers a concrete adapter afterward
/// (<c>AddKgsmAdapters</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class UnavailableEventHistory : IEventHistory
{
    public Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EventHistoryReading(AuditReadState.MonitorUnavailable, Array.Empty<AuditEventRow>()));
}
