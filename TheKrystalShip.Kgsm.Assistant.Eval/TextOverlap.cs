using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// A deterministic lexical-overlap measure used only to DIAGNOSE retrieval, never to score answers.
/// Given a gold passage and a retrieved chunk's text, <see cref="Coverage"/> reports the fraction of
/// the gold's distinct content tokens that also appear in the chunk — a recall-flavoured proxy for
/// "does this chunk actually contain the gold passage". It is intentionally simple (lowercase, split
/// on non-alphanumerics, drop very short tokens and a tiny stop set) so the read is transparent and
/// threshold choices are easy to eyeball; it is NOT semantic similarity (that's the embedder's job).
/// </summary>
internal static partial class TextOverlap
{
    // Deliberately tiny — just the function words that would otherwise inflate coverage for any chunk.
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "her", "was", "one",
        "our", "out", "had", "has", "his", "how", "its", "who", "did", "yes", "she", "him", "this",
        "that", "with", "from", "they", "have", "what", "when", "your", "than", "then", "them", "into",
        "over", "only", "also", "such", "each", "been", "were", "will", "would", "could", "should",
        "their", "there", "which", "while", "where", "these", "those", "about",
    };

    /// <summary>
    /// Fraction (0..1) of <paramref name="gold"/>'s distinct content tokens that appear in
    /// <paramref name="text"/>. Empty gold → 0 (nothing to cover).
    /// </summary>
    public static double Coverage(string? gold, string? text)
    {
        var goldTokens = Tokenize(gold);
        if (goldTokens.Count == 0)
            return 0;

        var textTokens = Tokenize(text);
        if (textTokens.Count == 0)
            return 0;

        var hit = 0;
        foreach (var t in goldTokens)
            if (textTokens.Contains(t))
                hit++;

        return (double)hit / goldTokens.Count;
    }

    /// <summary>Distinct content tokens: lowercased alphanumeric runs of length ≥ 3, minus the stop set.</summary>
    private static HashSet<string> Tokenize(string? s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(s))
            return set;

        foreach (Match m in TokenRegex().Matches(s.ToLowerInvariant()))
        {
            var tok = m.Value;
            if (tok.Length >= 3 && !Stop.Contains(tok))
                set.Add(tok);
        }
        return set;
    }

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex TokenRegex();
}
