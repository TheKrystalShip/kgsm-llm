using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IEventHistory"/> port by reading the engine's event journal
/// through kgsm-lib.
/// </summary>
/// <remarks>
/// <para>
/// The journal is the record of what the engine did, so this reads it directly rather than asking
/// another service what it remembers. That keeps the leaf's boundary intact — it depends on kgsm-lib
/// and a local Ollama, and on nothing else — and it means the audit tools work on a host running no
/// other leaf at all.
/// </para>
/// <para>
/// Per the port contract this NEVER throws. An absent or unreadable journal maps to
/// <see cref="AuditReadState.JournalUnavailable"/>, which the composers must not narrate as "nothing
/// happened".
/// </para>
/// </remarks>
internal sealed class KgsmEventHistory : IEventHistory
{
    private readonly IEventJournalHistory _journal;
    private readonly ILogger<KgsmEventHistory> _logger;

    public KgsmEventHistory(IEventJournalHistory journal, ILogger<KgsmEventHistory> logger)
    {
        _journal = journal;
        _logger = logger;
    }

    public async Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default)
    {
        EventHistoryPage page;
        try
        {
            page = await _journal.QueryAsync(new EventHistoryQuery
            {
                Instance = string.IsNullOrWhiteSpace(instance) ? null : instance,
                SinceMs = sinceMs,
                Limit = limit
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reader's contract is that it does not throw for a missing or unreadable journal;
            // this covers it breaking that contract anyway, since the port's own contract is stricter.
            _logger.LogDebug(ex, "event journal read failed for instance '{Instance}'", instance ?? "(all)");
            return Unavailable;
        }

        if (!page.JournalReadable)
        {
            _logger.LogDebug("event journal unreadable for instance '{Instance}'", instance ?? "(all)");
            return Unavailable;
        }

        AuditEventRow[] rows = [.. page.Events.Select(e =>
            new AuditEventRow(e.Id, e.Ts, e.Type, e.Instance, e.Actor, e.Origin, e.Blueprint))];

        return new EventHistoryReading(AuditReadState.Available, rows);
    }

    private static EventHistoryReading Unavailable =>
        new(AuditReadState.JournalUnavailable, []);
}
