using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Short-TTL per-user cache of the "holds the action role?" decision, so resolving authority
/// on every <c>/turn</c> (and re-checking at <c>/confirm</c>) doesn't hammer Discord's
/// rate-limited member endpoint. A miss falls through to a live lookup; a brief per-user
/// stampede on expiry is acceptable for a small home service, so there's no locking.
/// </summary>
internal sealed class RoleCache
{
    private sealed record Entry(bool HasActionRole, DateTimeOffset FetchedUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _ttlSeconds;

    public RoleCache(IOptions<AuthOptions> options)
    {
        _ttlSeconds = options.Value.RoleCacheTtlSeconds > 0 ? options.Value.RoleCacheTtlSeconds : 60;
    }

    /// <summary>Returns a cached decision for the user if it's still within the TTL.</summary>
    public bool TryGet(string userId, out bool hasActionRole)
    {
        hasActionRole = false;
        if (!_entries.TryGetValue(userId, out var entry))
            return false;

        if (DateTimeOffset.UtcNow - entry.FetchedUtc > TimeSpan.FromSeconds(_ttlSeconds))
        {
            _entries.TryRemove(userId, out _);
            return false;
        }

        hasActionRole = entry.HasActionRole;
        return true;
    }

    public void Set(string userId, bool hasActionRole) =>
        _entries[userId] = new Entry(hasActionRole, DateTimeOffset.UtcNow);

    /// <summary>Drops a user's cached decision (e.g. on logout / session eviction).</summary>
    public void Remove(string userId) => _entries.TryRemove(userId, out _);
}
