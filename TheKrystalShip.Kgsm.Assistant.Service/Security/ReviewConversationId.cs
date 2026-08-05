using System.Text;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// The opaque handle the review surface uses to address one stored conversation.
/// <para>
/// The user-facing endpoints compose their key from the verified principal (<c>web:{userId}:{chat}</c>),
/// so a client can only ever name its own. The review surface has no such anchor — it reads across
/// users by design — so the id it accepts is minted by the listing and handed back verbatim:
/// base64url of the stored conversation id. That keeps the store's <c>:</c>-bearing key out of the
/// route, and keeps the client from CONSTRUCTING a key rather than choosing one it was offered.
/// </para>
/// <para>
/// It is an opaque handle, not a secret: it is reversible by design and carries no authority. What
/// keeps one user's conversation out of another's hands is the admin gate in front of the endpoint,
/// plus <see cref="TryDecode"/> refusing anything outside the surface it is scoped to.
/// </para>
/// </summary>
internal static class ReviewConversationId
{
    /// <summary>The stored id, base64url-encoded (no padding) so it is a single safe path segment.</summary>
    public static string Encode(string conversationId) =>
        Base64Url.Encode(Encoding.UTF8.GetBytes(conversationId));

    /// <summary>
    /// Reverses <see cref="Encode"/>, and only for a conversation under <paramref name="surfacePrefix"/>.
    /// False for anything malformed or outside the surface — the review surface is scoped to the ids it
    /// lists, so a decoded key naming some other namespace is refused rather than read.
    /// </summary>
    public static bool TryDecode(string? handle, string surfacePrefix, out string conversationId)
    {
        conversationId = string.Empty;
        if (string.IsNullOrWhiteSpace(handle))
            return false;

        byte[] bytes;
        try
        {
            bytes = Base64Url.Decode(handle);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var prefix = surfacePrefix.TrimEnd(':') + ":";
        if (!decoded.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        conversationId = decoded;
        return true;
    }

    /// <summary>
    /// The user segment of a stored id (<c>{surface}:{user}[:{chat}]</c>) — who the conversation
    /// belongs to. Empty when the id carries no user segment.
    /// </summary>
    public static string UserOf(string conversationId, string surfacePrefix)
    {
        var prefix = surfacePrefix.TrimEnd(':') + ":";
        if (!conversationId.StartsWith(prefix, StringComparison.Ordinal))
            return string.Empty;

        var rest = conversationId[prefix.Length..];
        var cut = rest.IndexOf(':');
        return cut < 0 ? rest : rest[..cut];
    }

    /// <summary>Projects a store summary into the review DTO, minting its opaque handle.</summary>
    public static AdminConversationDto ToDto(ConversationSummary s) =>
        new(Encode(s.ConversationId), s.Title, s.CreatedAt, s.LastActivityAt, s.TurnCount,
            s.Deleted, s.ErrorTurns, s.CapHitTurns);
}
