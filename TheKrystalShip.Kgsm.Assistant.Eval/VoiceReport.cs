using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>What one case's reply looked like under one style.</summary>
/// <param name="Chars">Reply length. On a spoken surface this IS the duration, at a fixed rate.</param>
/// <param name="Sentences">Terminated sentences — the second reading of "how much is being said".</param>
/// <param name="Markup">
/// Characters a speech synthesiser reads out or stumbles on: asterisks, backticks, list bullets, hashes,
/// pipes. Counted rather than judged — one is a defect on a voice surface and none on a screen.
/// </param>
/// <param name="Tools">The tools this turn actually called, in order.</param>
/// <param name="Staged">The confirmations this turn staged.</param>
/// <param name="SaysPending">Whether the reply tells the user something is waiting on them.</param>
/// <param name="Reply">The full reply, for reading — the transcript decides, the numbers only guide.</param>
internal sealed record VoiceSample(
    int Chars,
    int Sentences,
    int Markup,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Staged,
    bool SaysPending,
    string Reply)
{
    // Deliberately no seconds figure. The chars→seconds rate is a property of the synthesiser and the
    // voice, measured on the bot's own surface; inventing one here would be a fabricated metric.
    private static readonly Regex MarkupChars = new(@"[*`#|]|^\s*[-•]\s", RegexOptions.Multiline);
    private static readonly Regex SentenceEnd = new(@"[.!?](\s|$)");

    public static VoiceSample From(
        string reply, IReadOnlyList<string> tools, IReadOnlyList<string> staged) =>
        new(reply.Trim().Length,
            SentenceEnd.Matches(reply).Count,
            MarkupChars.Matches(reply).Count,
            tools,
            staged,
            PendingConfirmationNote.IsPresentIn(reply),
            reply.Trim());
}

/// <summary>One case measured under both styles, plus whether its floor held in each.</summary>
internal sealed record VoiceRow(
    string Id,
    string Prompt,
    bool Skipped,
    string? SkipReason,
    VoiceSample? Written,
    VoiceSample? Spoken,
    bool WrittenFloorHeld,
    bool SpokenFloorHeld)
{
    /// <summary>How much shorter the spoken reply is, as a fraction. Null when either side is missing.</summary>
    public double? Reduction => Written is { Chars: > 0 } w && Spoken is { } s
        ? 1.0 - ((double)s.Chars / w.Chars)
        : null;
}

/// <summary>Renders the spoken-reply measurement: the table, the aggregate, and the replies themselves.</summary>
internal static class VoiceReport
{
    public static void Render(IReadOnlyList<VoiceRow> rows, string model, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Spoken-reply length · corpus {VoiceSuite.Version} · model {model}");
        output.WriteLine(new string('─', 92));
        output.WriteLine($"{"id",-4} {"written",8} {"spoken",8} {"shorter",8}  {"markup",7}  {"floor",-11} prompt");
        output.WriteLine(new string('─', 92));

        foreach (var row in rows)
        {
            if (row.Skipped)
            {
                output.WriteLine($"{row.Id,-4} {"—",8} {"—",8} {"—",8}  {"—",7}  {"SKIP",-11} {row.Prompt}  ({row.SkipReason})");
                continue;
            }

            var reduction = row.Reduction is { } r ? $"{r * 100,6:0.0}%" : "—";
            var floor = row.WrittenFloorHeld && row.SpokenFloorHeld ? "ok"
                : !row.SpokenFloorHeld ? "SPOKEN FAIL"
                : "written fail";
            var markup = $"{row.Written?.Markup ?? 0}→{row.Spoken?.Markup ?? 0}";

            output.WriteLine(
                $"{row.Id,-4} {row.Written?.Chars ?? 0,8} {row.Spoken?.Chars ?? 0,8} {reduction,8}  " +
                $"{markup,7}  {floor,-11} {row.Prompt}");
        }

        output.WriteLine(new string('─', 92));

        var measured = rows.Where(r => !r.Skipped && r is { Written: not null, Spoken: not null }).ToList();
        if (measured.Count == 0)
        {
            output.WriteLine("Nothing was measured — every case was skipped.");
            return;
        }

        var writtenTotal = measured.Sum(r => r.Written!.Chars);
        var spokenTotal = measured.Sum(r => r.Spoken!.Chars);
        // The fleet-wide reduction is computed over the TOTALS, not as a mean of the per-row
        // percentages: a mean would weight a 40-character reply the same as a 400-character one, and
        // what a listener actually spends is the sum.
        var overall = writtenTotal == 0 ? 0 : 1.0 - ((double)spokenTotal / writtenTotal);

        output.WriteLine(
            $"{measured.Count} case(s): {writtenTotal} chars written → {spokenTotal} spoken " +
            $"({overall * 100:0.0}% shorter); median reply {Median(measured.Select(r => r.Written!.Chars))} → " +
            $"{Median(measured.Select(r => r.Spoken!.Chars))} chars.");
        output.WriteLine(
            $"Floors: {measured.Count(r => r.WrittenFloorHeld)}/{measured.Count} written, " +
            $"{measured.Count(r => r.SpokenFloorHeld)}/{measured.Count} spoken " +
            "(tool called, confirmation staged and stated where the case requires it).");
        output.WriteLine(
            $"Speakable markup: {measured.Sum(r => r.Written!.Markup)} chars written → " +
            $"{measured.Sum(r => r.Spoken!.Markup)} spoken.");
    }

    /// <summary>The replies themselves. The numbers guide; reading the two side by side decides.</summary>
    public static void RenderTranscript(IReadOnlyList<VoiceRow> rows, TextWriter output)
    {
        foreach (var row in rows.Where(r => !r.Skipped))
        {
            output.WriteLine();
            output.WriteLine($"── {row.Id} · {row.Prompt}");
            Sample("written", row.Written, row.WrittenFloorHeld);
            Sample("spoken ", row.Spoken, row.SpokenFloorHeld);
        }

        void Sample(string label, VoiceSample? sample, bool floorHeld)
        {
            if (sample is null)
            {
                output.WriteLine($"   {label}: (no reply)");
                return;
            }

            var trail = sample.Tools.Count == 0 && sample.Staged.Count == 0
                ? "(no tools)"
                : string.Join(" ", sample.Tools.Concat(sample.Staged.Select(s => $"staged:{s}")));
            output.WriteLine(
                $"   {label}: {sample.Chars} chars · {sample.Sentences} sentence(s) · {trail} · " +
                $"floor {(floorHeld ? "ok" : "FAIL")}");
            output.WriteLine($"      {sample.Reply.Replace("\n", "\n      ")}");
        }
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
