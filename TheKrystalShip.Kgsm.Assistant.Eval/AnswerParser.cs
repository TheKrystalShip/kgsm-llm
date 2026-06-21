using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Extracts the chosen letter from a free-text MCQ reply. The prompt asks the model to reason if it
/// wants and then end with a line <c>Answer: X</c> (reasoning-then-commit measurably helps a 12B), so
/// the parser is anchored on that marker and reads the LAST one — anything earlier is the model's
/// deliberation, not its verdict. Tiers degrade gracefully (explicit marker → "the answer is X" →
/// a bare trailing letter), and an out-of-range or absent letter is an honest parse FAILURE, never a
/// guess: the runner scores an unparseable reply wrong AND reports the parse-failure rate separately,
/// so a model that won't follow the format shows up as a format problem, not silent wrongness.
/// </summary>
internal static class AnswerParser
{
    // "Answer: X", "Answer - X", "Answer = X", "**Answer:** (X)", "answer is X" — the delimiter (or the
    // word "is") is REQUIRED, so prose like "answer in the docs" can't be misread as a letter choice.
    private static readonly Regex Marker = new(
        @"answer\s*(?:is\s+|[:\-=]\s*)\**\s*\(?\s*([a-z])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A line that is NOTHING but a single letter (optionally parenthesized/punctuated): "B", "(C)", "D.".
    // Deliberately line-anchored — scanning prose for a lone letter would misread a stray "a"/"I".
    private static readonly Regex BareLetterLine = new(
        @"^\s*\(?\s*([a-z])\s*\)?\s*[.):]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tries to read the chosen choice letter from <paramref name="reply"/>. <paramref name="choiceCount"/>
    /// bounds the valid range (A..A+count-1); a letter outside it is rejected. Returns the uppercase
    /// letter via <paramref name="letter"/>.
    /// </summary>
    public static bool TryParse(string? reply, int choiceCount, out char letter)
    {
        letter = '\0';
        if (string.IsNullOrWhiteSpace(reply) || choiceCount <= 0)
            return false;

        var maxLetter = (char)('A' + Math.Min(choiceCount, 26) - 1);

        // Tier 1 + 2: the explicit marker, last occurrence (the model's final commitment).
        if (LastMarkerInRange(reply, maxLetter, out letter))
            return true;

        // Tier 3: a reply whose last non-empty line is just a letter (a terse "B" with no marker).
        var lines = reply.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            var m = BareLetterLine.Match(lines[i]);
            if (m.Success)
            {
                var c = char.ToUpperInvariant(m.Groups[1].Value[0]);
                if (c >= 'A' && c <= maxLetter)
                {
                    letter = c;
                    return true;
                }
            }
            break; // only inspect the last non-empty line
        }
        return false;
    }

    private static bool LastMarkerInRange(string text, char maxLetter, out char letter)
    {
        letter = '\0';
        foreach (Match m in Marker.Matches(text))
        {
            var c = char.ToUpperInvariant(m.Groups[1].Value[0]);
            if (c >= 'A' && c <= maxLetter)
                letter = c; // keep the last in-range hit
        }
        return letter != '\0';
    }
}
