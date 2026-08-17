namespace TheKrystalShip.Kgsm.Assistant.Files;

/// <summary>How a proposed setting change landed against a file's current content.</summary>
public enum SettingEditOutcome
{
    /// <summary>The key occurred exactly once and its value was replaced.</summary>
    Applied,

    /// <summary>The key is nowhere in the content — nothing is guessed, nothing is written.</summary>
    NoMatch,

    /// <summary>The key occurs in more than one place, so which value was meant is unknown.</summary>
    Ambiguous,

    /// <summary>The key is empty or is not a key at all, which would match everywhere and nowhere.</summary>
    NoKey,

    /// <summary>The new value is the value already there — the edit changes nothing.</summary>
    NoChange,
}

/// <summary>
/// The result of <see cref="SettingEdit.Apply"/>: the outcome, the new content when it applied, how many
/// places the key occurs, and the value that was there before — which is what lets a caller report the
/// change as <c>before → after</c> rather than merely announcing that something was staged.
/// </summary>
/// <param name="Outcome">Which of the five cases this edit is.</param>
/// <param name="Content">The full new content — non-null only when <see cref="SettingEditOutcome.Applied"/>.</param>
/// <param name="Matches">How many times the key occurs in the content.</param>
/// <param name="PreviousValue">The value the key held — non-null when the key occurred exactly once.</param>
public sealed record SettingEditResult(
    SettingEditOutcome Outcome, string? Content, int Matches, string? PreviousValue)
{
    public bool IsApplied => Outcome == SettingEditOutcome.Applied;

    internal static SettingEditResult Refused(SettingEditOutcome outcome, int matches = 0) =>
        new(outcome, null, matches, null);
}

/// <summary>
/// Replaces the value of one <c>Key=Value</c> setting, addressing it by the key rather than by the text
/// around it. <see cref="FileEdit"/> asks a caller to reproduce the text it is replacing; that works for
/// a line-oriented config and fails for a packed one, where a single line holds every setting the game
/// has. Palworld's <c>PalWorldSettings.ini</c> is the case that forces this: its
/// <c>OptionSettings=(…)</c> line is around two thousand characters, so changing one number means
/// echoing the other fifty settings back byte-perfect, and a small model reliably mangles a key or a
/// decimal somewhere in the middle. Naming the key sends a dozen characters instead of two thousand,
/// and the value already on disk is read rather than retyped.
/// <para>
/// A key is matched as a whole token immediately before its <c>=</c>, so <c>Rate</c> does not match
/// inside <c>ExpRate</c>. The value runs to the first <c>,</c>, <c>)</c> or line end, which is what
/// makes one call work on both a packed line and a plain <c>key=value</c> file; a quoted value keeps
/// whatever those characters mean inside its quotes. Whitespace around the value is left where it is.
/// </para>
/// <para>
/// It refuses on the same terms <see cref="FileEdit"/> does: a key that occurs nowhere, or in more than
/// one place, changes nothing and is reported. Guessing which of two matches was meant is the one thing
/// an editor addressed by name must never do, because the caller cannot see that it guessed.
/// </para>
/// <para>Matching is ordinal for the value and case-insensitive for the key: games spell their own
/// settings inconsistently across documentation, while the value is bytes and stays bytes.</para>
/// </summary>
public static class SettingEdit
{
    public static SettingEditResult Apply(string content, string key, string value)
    {
        key = key.Trim();
        if (key.Length == 0 || !IsKeyToken(key))
            return SettingEditResult.Refused(SettingEditOutcome.NoKey);

        var spans = Locate(content, key);
        if (spans.Count == 0)
            return SettingEditResult.Refused(SettingEditOutcome.NoMatch);
        if (spans.Count > 1)
            return SettingEditResult.Refused(SettingEditOutcome.Ambiguous, spans.Count);

        var (start, length) = spans[0];
        var previous = content.Substring(start, length);
        if (string.Equals(previous, value, StringComparison.Ordinal))
            return new SettingEditResult(SettingEditOutcome.NoChange, null, 1, previous);

        var edited = string.Concat(content.AsSpan(0, start), value, content.AsSpan(start + length));
        return new SettingEditResult(SettingEditOutcome.Applied, edited, 1, previous);
    }

    /// <summary>
    /// A key is a bare identifier. Anything else — a fragment with an <c>=</c> in it, a whole line, a
    /// path — is a caller that meant <see cref="FileEdit"/>, and matching it loosely here would edit
    /// something it did not name.
    /// </summary>
    private static bool IsKeyToken(string key)
    {
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Every place the key is assigned, as the span of its value. Stops at two matches: the caller only
    /// distinguishes none / one / more than one, and a key in a large file has no reason to be walked to
    /// the end just to be refused.
    /// </summary>
    private static List<(int Start, int Length)> Locate(string content, string key)
    {
        var found = new List<(int, int)>();

        for (var from = 0; from <= content.Length - key.Length;)
        {
            var at = content.IndexOf(key, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                break;

            from = at + key.Length;

            // The character before the key must not continue an identifier, or "Rate" matches inside
            // "ExpRate" and the wrong setting is edited under the right name.
            if (at > 0 && IsIdentifierChar(content[at - 1]))
                continue;

            var i = at + key.Length;
            while (i < content.Length && (content[i] == ' ' || content[i] == '\t'))
                i++;
            if (i >= content.Length || content[i] != '=')
                continue;

            i++;
            while (i < content.Length && (content[i] == ' ' || content[i] == '\t'))
                i++;

            found.Add((i, ValueLength(content, i)));
            if (found.Count > 1)
                break;
        }

        return found;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// How far the value at <paramref name="start"/> runs. A quoted value ends at its closing quote, so
    /// a comma inside it stays part of the value; anything else ends at the first separator a packed
    /// line uses or at the end of the line. Trailing spaces are left outside the span so replacing the
    /// value does not reflow what follows it.
    /// </summary>
    private static int ValueLength(string content, int start)
    {
        if (start < content.Length && content[start] == '"')
        {
            for (var i = start + 1; i < content.Length; i++)
            {
                if (content[i] == '\\') { i++; continue; }
                if (content[i] == '"') return i - start + 1;
                if (content[i] is '\r' or '\n') break;
            }

            // An unterminated quote is a malformed line; treat the quote as an ordinary character
            // rather than swallowing the rest of the file.
        }

        var end = start;
        while (end < content.Length && content[end] is not (',' or ')' or '\r' or '\n'))
            end++;

        while (end > start && (content[end - 1] == ' ' || content[end - 1] == '\t'))
            end--;

        return end - start;
    }
}
