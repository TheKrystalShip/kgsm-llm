using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A short, stable content hash used to fingerprint prompt text for transcript analysis (e.g.
/// bucketing turns "before vs after" a system-prompt edit). Deterministic across runs/processes so
/// the same text always yields the same id. Not security-sensitive — just a compact change marker.
/// </summary>
public static class PromptHash
{
    /// <summary>Returns the first 8 lowercase hex chars of the SHA-256 of <paramref name="text"/>.</summary>
    public static string Short(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
