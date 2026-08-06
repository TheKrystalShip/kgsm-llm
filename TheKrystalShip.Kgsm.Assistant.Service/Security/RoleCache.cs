using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Short-TTL per-user cache of the tier a caller holds, so resolving authority on every <c>/turn</c>
/// (and re-checking at <c>/confirm</c>, and on a review read) doesn't hammer Discord's rate-limited
/// member endpoint. A miss falls through to a live lookup; a brief per-user stampede on expiry is
/// acceptable for a small home service, so there's no locking.
/// <para>
/// One entry per user, because authority is one ordered tier: a single lookup of the member's roles
/// answers every question this service asks — may they act, may they review — so there is nothing
/// left for a per-role key to keep apart.
/// </para>
/// </summary>
/// <remarks>
/// The TTL is the staleness bound on a revoked role: someone whose role is taken away keeps the tier
/// they held until their entry expires. Authority is deliberately never stored on the session, so
/// this is the only place a stale answer can survive, and the TTL is the whole knob.
/// </remarks>
internal sealed class RoleCache
{
    private sealed record Entry(KgsmTier Tier, DateTimeOffset FetchedUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _ttlSeconds;

    public RoleCache(IOptions<AuthOptions> options)
    {
        _ttlSeconds = options.Value.RoleCacheTtlSeconds > 0 ? options.Value.RoleCacheTtlSeconds : 60;
    }

    /// <summary>Returns this user's cached tier if it is still within the TTL.</summary>
    public bool TryGet(string userId, out KgsmTier tier)
    {
        tier = KgsmTier.None;
        if (!_entries.TryGetValue(userId, out Entry? entry))
            return false;

        if (DateTimeOffset.UtcNow - entry.FetchedUtc > TimeSpan.FromSeconds(_ttlSeconds))
        {
            _entries.TryRemove(userId, out _);
            return false;
        }

        tier = entry.Tier;
        return true;
    }

    public void Set(string userId, KgsmTier tier) =>
        _entries[userId] = new Entry(tier, DateTimeOffset.UtcNow);

    /// <summary>Drops a user's cached tier (e.g. on logout / session eviction).</summary>
    public void Remove(string userId) => _entries.TryRemove(userId, out _);
}
