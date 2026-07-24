namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// The operator-configured allow/deny overlay on top of the (non-optional) <see cref="SsrfGuard"/> —
/// <c>WebFetchOptions.AllowedHosts</c>/<c>DeniedHosts</c> aren't a safety boundary themselves (the
/// SSRF guard is that), just an extra scoping knob (e.g. "only ever fetch docs.example.com").
/// Denylist wins over allowlist; an empty allowlist means "no restriction beyond the SSRF guard."
/// </summary>
internal static class HostPolicy
{
    /// <summary>True when <paramref name="host"/> equals <paramref name="pattern"/> or is a
    /// subdomain of it (e.g. pattern "github.com" matches "api.github.com" but not
    /// "notgithub.com").</summary>
    public static bool Matches(string host, string pattern) =>
        string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks <paramref name="host"/> against the operator's optional allow/deny lists.
    /// Returns false (allowed) with <paramref name="reason"/> null when there's no policy objection;
    /// true (blocked) with a human-readable reason otherwise.</summary>
    public static bool IsBlocked(string host, IReadOnlyList<string> allowedHosts, IReadOnlyList<string> deniedHosts, out string? reason)
    {
        foreach (var denied in deniedHosts)
        {
            if (Matches(host, denied))
            {
                reason = $"'{host}' is on the operator's fetch denylist";
                return true;
            }
        }

        if (allowedHosts.Count > 0 && !allowedHosts.Any(allowed => Matches(host, allowed)))
        {
            reason = $"'{host}' is not on the operator's fetch allowlist";
            return true;
        }

        reason = null;
        return false;
    }
}
