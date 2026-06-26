using System.Text;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Normalises a client-supplied per-chat conversation id into a safe memory-key segment.
/// <para>
/// The conversation (memory) key is ALWAYS <c>web:{serverDerivedUserId}[:{thisSegment}]</c> — the
/// user-id prefix is authoritative and set server-side from the authenticated/forwarded identity, so a
/// sanitised sub-segment can only ever partition a caller's OWN memory; it can never reach another
/// user's namespace. Sanitising additionally keeps the value header-safe and bounds the key space.
/// </para>
/// </summary>
internal static class ConversationScope
{
    /// <summary>Upper bound on a conversation sub-id; the SPA's ids are ~12 chars, this is slack.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Returns the id reduced to <c>[A-Za-z0-9_-]</c> and capped at <see cref="MaxLength"/>, or
    /// <see langword="null"/> when the input is null/blank/all-stripped. A <c>null</c> result makes the
    /// caller fall back to the bare per-user key (<c>web:{userId}</c>), preserving the pre-fix behaviour
    /// for clients that send no chat id.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var sb = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (var c in raw)
        {
            if (sb.Length == MaxLength)
                break;
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}
