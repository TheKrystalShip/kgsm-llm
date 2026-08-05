using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Short-TTL per-user cache of a "holds this role?" decision, so resolving authority on every
/// <c>/turn</c> (and re-checking at <c>/confirm</c>, and on an admin review read) doesn't hammer
/// Discord's rate-limited member endpoint. A miss falls through to a live lookup; a brief per-user
/// stampede on expiry is acceptable for a small home service, so there's no locking.
/// <para>
/// Entries are keyed by <em>role AND user</em>: the service asks about more than one role (the action
/// role, the review role), and a single per-user slot would let one role's answer be served for
/// another's question.
/// </para>
/// </summary>
internal sealed class RoleCache
{
    private sealed record Entry(bool HasRole, DateTimeOffset FetchedUtc);

    private readonly ConcurrentDictionary<(string RoleId, string UserId), Entry> _entries = new();
    private readonly int _ttlSeconds;

    public RoleCache(IOptions<AuthOptions> options)
    {
        _ttlSeconds = options.Value.RoleCacheTtlSeconds > 0 ? options.Value.RoleCacheTtlSeconds : 60;
    }

    /// <summary>Returns a cached decision for this (role, user) if it's still within the TTL.</summary>
    public bool TryGet(string roleId, string userId, out bool hasRole)
    {
        hasRole = false;
        if (!_entries.TryGetValue((roleId, userId), out var entry))
            return false;

        if (DateTimeOffset.UtcNow - entry.FetchedUtc > TimeSpan.FromSeconds(_ttlSeconds))
        {
            _entries.TryRemove((roleId, userId), out _);
            return false;
        }

        hasRole = entry.HasRole;
        return true;
    }

    public void Set(string roleId, string userId, bool hasRole) =>
        _entries[(roleId, userId)] = new Entry(hasRole, DateTimeOffset.UtcNow);

    /// <summary>Drops every cached role decision for a user (e.g. on logout / session eviction).</summary>
    public void Remove(string userId)
    {
        foreach (var key in _entries.Keys.Where(k => k.UserId == userId))
            _entries.TryRemove(key, out _);
    }
}
