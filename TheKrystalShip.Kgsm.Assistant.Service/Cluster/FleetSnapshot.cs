using System.Collections.Concurrent;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// The last answer a fan-out gave one person, reused for a short while.
/// </summary>
/// <remarks>
/// <para>
/// A single turn reads the fleet several times — the prompt is built from it, then a tool resolves a
/// name against it, then another tool does the same. Asking every node again for each of those turns
/// one question into a burst, so an answer is held briefly and reused.
/// </para>
/// <para>
/// <b>Held per person, because who asked decides what answered.</b> A node that holds no account for
/// somebody refuses them and is reported to them as unreached; handing that person somebody else's
/// answer would show them a machine they cannot actually reach, and handing the reverse would hide one
/// they can.
/// </para>
/// <para>
/// <b>The window is seconds, not minutes.</b> Nothing tells this member that another machine installed
/// a server, so the only thing bounding how stale an answer gets is how long it is kept — unlike the
/// engine on this machine, which says so as it happens.
/// </para>
/// </remarks>
internal sealed class FleetSnapshot<T>(TimeSpan ttl)
{
    /// <summary>Above this many people, the expired entries are swept on the next write.</summary>
    private const int SweepAbove = 64;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, T Value)> _byCaller =
        new(StringComparer.Ordinal);

    /// <summary>The fleet as this caller last saw it, reading again when that is older than the window.</summary>
    public async Task<T> ReadAsync(string caller, Func<Task<T>> read)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_byCaller.TryGetValue(caller, out (DateTimeOffset At, T Value) held) && now - held.At < ttl)
            return held.Value;

        T value = await read().ConfigureAwait(false);

        _byCaller[caller] = (DateTimeOffset.UtcNow, value);
        if (_byCaller.Count > SweepAbove)
        {
            foreach ((string other, (DateTimeOffset at, _)) in _byCaller)
                if (DateTimeOffset.UtcNow - at >= ttl)
                    _byCaller.TryRemove(other, out _);
        }

        return value;
    }

    /// <summary>Forget every held answer, so the next read asks the nodes again.</summary>
    public void Clear() => _byCaller.Clear();
}
