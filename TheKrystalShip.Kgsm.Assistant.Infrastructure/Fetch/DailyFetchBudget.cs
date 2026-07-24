using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// Process-wide daily ceiling on outbound <c>fetch_url</c> calls — mirrors
/// <c>TheKrystalShip.Kgsm.Assistant.Infrastructure.Search.DailyCallBudget</c> exactly (kept as a
/// separate class, not a shared generic, so the two wallets stay independent counters against their
/// own options — a search-budget change must not silently move the fetch ceiling). Because
/// <c>fetch_url</c> is offered to everyone (the read-only tier), this is the only spend guard in
/// front of an unbounded number of outbound HTTP requests, so it is deliberately global state — a
/// singleton, not per-turn or per-caller. Resets at UTC midnight.
/// </summary>
public sealed class DailyFetchBudget
{
    private readonly int _maxPerDay;
    private readonly object _gate = new();
    private DateOnly _day;
    private int _count;

    public DailyFetchBudget(IOptions<WebFetchOptions> options)
    {
        _maxPerDay = options.Value.MaxCallsPerDay;
        _day = DateOnly.FromDateTime(DateTime.UtcNow);
    }

    /// <summary>
    /// Reserves one call against today's budget. Returns false (reserving nothing) once today's
    /// ceiling is reached; the counter rolls over on the first call of a new UTC day.
    /// </summary>
    public bool TryConsume()
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _day)
            {
                _day = today;
                _count = 0;
            }

            if (_count >= _maxPerDay)
                return false;

            _count++;
            return true;
        }
    }
}
