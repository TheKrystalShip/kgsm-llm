using System.Text;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Normalises the slug a memory is filed under. A key is chosen by whoever writes the memory — the
/// model, most often — so it arrives as prose and has to be reduced to something that can be matched
/// exactly, because matching is what makes rewriting a memory supersede it rather than duplicate it.
/// </summary>
/// <remarks>
/// Lowercased, reduced to <c>[a-z0-9-]</c> with runs of anything else collapsing to a single hyphen,
/// trimmed of leading/trailing hyphens and capped. Two spellings of the same intent
/// (<c>"Factorio for tests"</c>, <c>"factorio_for_tests"</c>) therefore land on one key, which is the
/// point: a model that reaches for a memory it wrote last week will not reproduce the punctuation.
/// </remarks>
public static class MemoryKey
{
    /// <summary>Upper bound on a key. Long enough to stay readable in a listing, short enough that a
    /// model reliably reproduces one it was shown.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The normalised form of <paramref name="raw"/>, or <see langword="null"/> when nothing usable
    /// survives (blank, or punctuation alone). The caller refuses a null rather than inventing a key:
    /// a memory filed under a name nobody can name again cannot be rewritten or forgotten.
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

            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                // A run of separators is one boundary, not several — so "a  b" and "a_-_b" agree.
                sb.Append('-');
            }
        }

        // A trailing hyphen is the separator of a word the cap cut off; it names no boundary.
        while (sb.Length > 0 && sb[^1] == '-')
            sb.Length--;

        return sb.Length == 0 ? null : sb.ToString();
    }
}
