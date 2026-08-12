namespace TheKrystalShip.Kgsm.Assistant.Consoles;

/// <summary>A recognised way a process announces it is dying, and the line that said so.</summary>
public readonly record struct FatalHit(string Description, string Line);

/// <summary>
/// Reading a dead run's last words: what counts as a process announcing its own death, and how much
/// of the output around it to quote.
/// <para>
/// Shared by every tool that reports a crash, so they cannot disagree about what "fatal" means. Two
/// copies of a table like this drift — one gains a signature the other lacks, and the same crash then
/// reads as diagnosed by one tool and unexplained by another, with nothing to say which is right.
/// </para>
/// </summary>
public static class CrashOutput
{
    /// <summary>
    /// Phrases a runtime prints only on the way out, each with how to describe it. Matched
    /// case-insensitively against whole lines.
    /// <para>
    /// <b>Kept deliberately narrow.</b> Game servers log alarming words while perfectly healthy —
    /// a missing texture is an <c>ERROR</c> and a failed optional plugin is a "fatal error" the
    /// process survives. Every entry here is something a runtime says as it dies, not something a
    /// program says about a problem it handled. Widening the table trades a rare miss for routine
    /// false diagnoses, which is much worse: a wrong cause stated confidently stops the search.
    /// </para>
    /// </summary>
    private static readonly (string Needle, string Description)[] FatalSignatures =
    [
        ("unhandled exception", "an unhandled exception"),
        ("unhandled error", "an unhandled error"),
        ("exception in thread", "an exception that killed a thread"),
        ("terminate called after throwing", "an uncaught C++ exception"),
        ("segmentation fault", "a segmentation fault"),
        ("segfault", "a segmentation fault"),
        ("core dumped", "a core dump"),
        ("out of memory", "an out-of-memory condition"),
        ("outofmemoryerror", "an out-of-memory condition"),
        ("stack overflow", "a stack overflow"),
        ("stackoverflowexception", "a stack overflow"),
        ("panic:", "a panic"),
        ("fatal error", "a fatal error"),
        ("fatal exception", "a fatal exception"),
    ];

    /// <summary>How many trailing lines an excerpt keeps — enough for a stack trace's origin.</summary>
    public const int MaxExcerptLines = 18;

    /// <summary>Per-line cap inside an excerpt — one enormous line must not crowd out the rest.</summary>
    public const int MaxLineLength = 220;

    /// <summary>
    /// The LAST recognised fatal line in the run, or null when nothing matched. Scanned from the end
    /// because a long-lived server may have survived something alarming earlier in the same run; what
    /// killed it is what it said last.
    /// </summary>
    public static FatalHit? FindFatalSignature(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            foreach (var (needle, description) in FatalSignatures)
            {
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return new FatalHit(description, lines[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// The tail of a run's output, bounded so a stack trace fits without a runaway log filling the
    /// model's context. Trailing blank lines are dropped so the excerpt ends on something readable.
    /// </summary>
    public static IReadOnlyList<string> TailExcerpt(
        IReadOnlyList<string> lines, int maxLines = MaxExcerptLines)
    {
        var end = lines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1]))
            end--;

        var start = Math.Max(0, end - maxLines);
        var excerpt = new List<string>(end - start);
        for (var i = start; i < end; i++)
            excerpt.Add(Clip(lines[i]));

        return excerpt;
    }

    /// <summary>Keeps one quoted console line from crowding out whatever it appears beside.</summary>
    public static string Clip(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.Length <= MaxLineLength ? trimmed : trimmed[..MaxLineLength] + "…";
    }
}
