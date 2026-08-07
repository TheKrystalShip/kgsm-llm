using TheKrystalShip.Kgsm.Assistant.Audit;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// A read of raw engine events from the engine's event journal, as the
/// <see cref="IEventHistory"/> port returns it. <see cref="AuditData.State"/> carries the outcome
/// (<see cref="AuditReadState.Available"/> with rows — possibly none — or
/// <see cref="AuditReadState.JournalUnavailable"/> with an empty list); failure is data, never an
/// exception. <see cref="Events"/> is ts-DESC (most-recent-first), unfiltered by event type — the
/// caller (the <c>get_audit_log</c> / <c>get_change_timeline</c> composers) decides what subset it needs.
/// </summary>
public sealed record EventHistoryReading(AuditReadState State, IReadOnlyList<AuditEventRow> Events);

/// <summary>
/// Reads KGSM <em>engine</em> event history from the engine's own event journal — the record of what
/// happened — never via kgsm-api or another leaf. Backs the model-facing <c>get_audit_log</c>,
/// <c>get_change_timeline</c> and <c>trace_root_cause</c> tools.
/// <para>
/// Because the journal is a file the engine writes, this needs nothing else running: the audit tools
/// answer on a host with no other leaf installed, which is not true of the metrics tools beside them.
/// Fails closed — an unreadable journal returns <see cref="AuditReadState.JournalUnavailable"/> — and
/// implementations MUST NOT throw.
/// </summary>
public interface IEventHistory
{
    /// <summary>
    /// Reads recent engine events, most-recent-first.
    /// </summary>
    /// <param name="instance">Scope to one instance's events; <see langword="null"/>/blank = every instance (fleet-wide).</param>
    /// <param name="sinceMs">Only events at or after this unix-ms timestamp; <see langword="null"/> = no lower bound.</param>
    /// <param name="limit">The maximum number of rows to return (the reader clamps this).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IEventHistory"/> for a host that has wired no engine at all: every read fails
/// closed as <see cref="AuditReadState.JournalUnavailable"/>, so embedding the assistant library never
/// breaks DI — mirrors <see cref="UnavailableServerMetrics"/>. <c>AddKgsmAssistant</c> registers this
/// with <c>TryAddSingleton</c>; <c>AddKgsmAdapters</c> registers the real reader afterward, and that
/// later registration is the one resolved.
/// </summary>
internal sealed class UnavailableEventHistory : IEventHistory
{
    public Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EventHistoryReading(AuditReadState.JournalUnavailable, Array.Empty<AuditEventRow>()));
}
