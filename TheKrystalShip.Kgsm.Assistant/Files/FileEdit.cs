namespace TheKrystalShip.Kgsm.Assistant.Files;

/// <summary>How a proposed replacement landed against a file's current content.</summary>
public enum FileEditOutcome
{
    /// <summary>The anchor matched exactly once and was replaced.</summary>
    Applied,

    /// <summary>The anchor is nowhere in the content — nothing is guessed, nothing is written.</summary>
    NoMatch,

    /// <summary>The anchor matches in more than one place, so which one was meant is unknown.</summary>
    Ambiguous,

    /// <summary>The anchor is empty, which would match everywhere and nowhere.</summary>
    NoAnchor,

    /// <summary>The replacement is the anchor — the edit changes nothing.</summary>
    NoChange,
}

/// <summary>
/// The result of <see cref="FileEdit.Apply"/>: the outcome, the new content when it applied, and how
/// many places the anchor matched (what makes an ambiguous edit reportable rather than merely refused).
/// </summary>
/// <param name="Outcome">Which of the five cases this edit is.</param>
/// <param name="Content">The full new content — non-null only when <see cref="FileEditOutcome.Applied"/>.</param>
/// <param name="Matches">How many times the anchor occurs in the content.</param>
public sealed record FileEditResult(FileEditOutcome Outcome, string? Content, int Matches)
{
    public bool IsApplied => Outcome == FileEditOutcome.Applied;

    internal static FileEditResult Refused(FileEditOutcome outcome, int matches = 0) => new(outcome, null, matches);
}

/// <summary>
/// Applies one anchored replacement to a file's content: find <c>oldText</c>, put <c>newText</c> in its
/// place, leave every other byte exactly as it was. This is what lets a write carry only the part that
/// changes — the model names the text to replace, and the bytes it never sent are the bytes already on
/// disk, so nothing can be dropped, truncated or reworded on the way through a model.
/// <para>
/// It is deliberately unforgiving about the anchor. An anchor that matches nowhere, matches several
/// places, is empty, or equals its own replacement is <b>refused</b>, never approximated: the caller
/// reports why and the model re-reads and copies the text exactly, which is the honest outcome. A
/// "closest match" here would be a silent guess at which line a person meant to change.
/// </para>
/// <para>Matching is ordinal — a config file's bytes, compared as bytes, with no culture or case
/// folding in the way.</para>
/// </summary>
public static class FileEdit
{
    public static FileEditResult Apply(string content, string oldText, string newText)
    {
        if (oldText.Length == 0)
            return FileEditResult.Refused(FileEditOutcome.NoAnchor);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return FileEditResult.Refused(FileEditOutcome.NoChange);

        var matches = Count(content, oldText);
        if (matches == 0)
            return FileEditResult.Refused(FileEditOutcome.NoMatch);
        if (matches > 1)
            return FileEditResult.Refused(FileEditOutcome.Ambiguous, matches);

        var at = content.IndexOf(oldText, StringComparison.Ordinal);
        var edited = string.Concat(
            content.AsSpan(0, at), newText, content.AsSpan(at + oldText.Length));

        return new FileEditResult(FileEditOutcome.Applied, edited, 1);
    }

    /// <summary>
    /// Counts non-overlapping occurrences. It stops at two: the caller only distinguishes none / one /
    /// more than one, and an anchor like a single newline in a large file has no reason to be walked
    /// to the end just to be refused.
    /// </summary>
    private static int Count(string content, string oldText)
    {
        var found = 0;
        var from = 0;
        while (from <= content.Length - oldText.Length)
        {
            var at = content.IndexOf(oldText, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            if (++found > 1)
                break;
            from = at + oldText.Length;
        }

        return found;
    }
}
