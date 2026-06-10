using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Tracks the in-flight Discord authorize→callback handshake. For each login the server
/// generates a CSRF <c>state</c> and a PKCE <c>code_verifier</c>; the verifier stays here
/// (the SPA never sees it) keyed by the state. The callback consumes the state ONCE — a
/// replayed or expired state is rejected, which is the callback's CSRF/mix-up protection.
/// </summary>
internal sealed class OAuthStateStore
{
    private sealed record Entry(string CodeVerifier, DateTimeOffset CreatedUtc);

    private readonly ConcurrentDictionary<string, Entry> _states = new(StringComparer.Ordinal);
    private readonly int _ttlSeconds;

    public OAuthStateStore(IOptions<AuthOptions> options)
    {
        _ttlSeconds = options.Value.StateTtlSeconds > 0 ? options.Value.StateTtlSeconds : 300;
    }

    /// <summary>
    /// Stores the PKCE verifier under a fresh CSPRNG state value and returns the state.
    /// <c>/auth/login</c> is unauthenticated and internet-reachable, so an abandoned login
    /// (state created, callback never made) would otherwise leak forever — sweep expired
    /// entries opportunistically here to bound the dictionary.
    /// </summary>
    public string Create(string codeVerifier)
    {
        SweepExpired();
        var state = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _states[state] = new Entry(codeVerifier, DateTimeOffset.UtcNow);
        return state;
    }

    /// <summary>Visible for tests: number of states currently held.</summary>
    internal int Count => _states.Count;

    private void SweepExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_ttlSeconds);
        foreach (var kvp in _states)
            if (kvp.Value.CreatedUtc < cutoff)
                _states.TryRemove(kvp.Key, out _);
    }

    /// <summary>
    /// Atomically removes and returns the verifier for a state — single use. Returns false if
    /// the state is unknown, already consumed, or older than the TTL.
    /// </summary>
    public bool TryConsume(string? state, out string codeVerifier)
    {
        codeVerifier = string.Empty;
        if (string.IsNullOrEmpty(state) || !_states.TryRemove(state, out var entry))
            return false;

        if (DateTimeOffset.UtcNow - entry.CreatedUtc > TimeSpan.FromSeconds(_ttlSeconds))
            return false; // consumed-but-expired: removing it above also cleans it up

        codeVerifier = entry.CodeVerifier;
        return true;
    }
}
