using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Search;

/// <summary>
/// Process-wide daily ceiling on outbound web searches — the wallet backstop. Because the
/// web_search tool is offered to everyone (it lives in the read-only tier), this is the only
/// spend guard standing in front of the provider's free-credit limit, so it is deliberately
/// global state — hence a singleton, not per-turn or per-caller. Resets at UTC midnight.
/// </summary>
public sealed class DailyCallBudget
{
    private readonly int _maxPerDay;
    private readonly object _gate = new();
    private DateOnly _day;
    private int _count;

    public DailyCallBudget(IOptions<WebSearchOptions> options)
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
