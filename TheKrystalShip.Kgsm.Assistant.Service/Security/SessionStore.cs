using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// A logged-in web session. Holds IDENTITY only — it deliberately carries NO retained Discord
/// token (authority is re-checked with the bot token, by user id) and NO
/// <c>canPerformActions</c> boolean (baking authority in would reintroduce a
/// staleness/escalation gap). Authority is a separate, short-TTL role lookup.
/// </summary>
internal sealed record Session(
    string DiscordUserId,
    string DisplayName,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// In-memory store of active web sessions keyed by an opaque bearer token. Thread-safe;
/// sessions are evicted lazily on read once expired (no background reaper for a single-instance
/// home service). Sessions are lost on restart, which simply forces a re-login. (Conversation
/// memory, by contrast, is now durable — see <c>SqliteConversationStore</c>.)
/// </summary>
internal sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>Stores the session under a fresh 256-bit CSPRNG bearer token and returns the token.</summary>
    public string Create(Session session)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = session;
        return token;
    }

    /// <summary>
    /// Resolves a session by its bearer token. Returns false (and evicts) if the token is
    /// unknown or the session has expired.
    /// </summary>
    public bool TryGet(string? token, out Session session)
    {
        session = null!;
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var found))
            return false;

        if (found.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        session = found;
        return true;
    }

    /// <summary>Removes a session (logout, or eviction after a token-expired re-check).</summary>
    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }
}
