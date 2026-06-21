using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Renders an <see cref="McqRun"/> as the lift chart Phase 5 exists to produce: a per-condition accuracy
/// table (closed-book → with-RAG → oracle), the headline lifts, a per-topic breakdown, any knob sweep,
/// and a built-in sanity line that calls out the failure the advisor flagged — if with-RAG ≈ oracle ≈
/// 100% and flat, the corpus is too easy to measure retrieval or tune knobs (add harder questions).
/// </summary>
internal static class McqScorecard
{
    public static void Render(McqRun run, TextWriter w)
    {
        var seed = run.Seed?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var t = run.Tuning;
        var topics = run.Items.Select(i => i.Topic).Distinct().Count();
        var questions = run.Conditions.Count > 0 ? run.Conditions[0].Total / Math.Max(run.Reps, 1) : 0;

        w.WriteLine();
        w.WriteLine($"═══ kgsm MCQ eval · {run.Model} · temp {Num(run.Temperature)} · seed {seed} · " +
                    $"reps {run.Reps} · corpus {run.CorpusVersion} ═══");
        w.WriteLine($"tuning: chunk {t.ChunkSize}/{t.ChunkOverlap} · topK {t.TopK} · minScore {Num(t.MinScore)} · " +
                    $"localMinScore {Num(t.LocalMinScore)} · maxCtx {t.MaxContextChars}");
        w.WriteLine($"corpus: {questions} question(s) across {topics} topic(s)");
        w.WriteLine();

        // Lift table.
        w.WriteLine($"{"CONDITION",-14} {"ACCURACY",-9} {"CORRECT",-9} UNPARSED");
        foreach (var c in run.Conditions)
            w.WriteLine($"{c.Condition.Label(),-14} {Pct(c.Accuracy),-9} {$"{c.Correct}/{c.Total}",-9} {c.Unparsed}");

        // Headline lifts (only the pairs we actually measured).
        w.WriteLine();
        var closed = Find(run, McqCondition.ClosedBook);
        var rag = Find(run, McqCondition.WithRag);
        var oracle = Find(run, McqCondition.Oracle);
        if (closed is not null && rag is not null)
            w.WriteLine($"lift: closed-book → with-rag  {Pts(rag.Accuracy - closed.Accuracy)}");
        if (rag is not null && oracle is not null)
            w.WriteLine($"gap:  with-rag → oracle       {Pts(oracle.Accuracy - rag.Accuracy)}  (retrieval headroom)");

        RenderByTopic(run, w);
        RenderSweeps(run, w);
        RenderSanity(run, w, rag, oracle);
    }

    private static void RenderByTopic(McqRun run, TextWriter w)
    {
        var topics = run.Items.Select(i => i.Topic).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (topics.Count <= 1)
            return;

        w.WriteLine();
        w.WriteLine("By topic (accuracy):");
        var header = $"{"TOPIC",-22}" + string.Concat(run.Conditions.Select(c => $"{c.Condition.Label(),-13}"));
        w.WriteLine(header);
        foreach (var topic in topics)
        {
            var row = $"{Clip(topic, 22),-22}";
            foreach (var c in run.Conditions)
            {
                var items = run.Items.Where(i => i.Topic == topic && i.Condition == c.Condition).ToList();
                var correct = items.Sum(i => i.Correct);
                var total = items.Sum(i => i.Reps);
                row += $"{(total == 0 ? "—" : Num((double)correct / total)),-13}";
            }
            w.WriteLine(row);
        }
    }

    private static void RenderSweeps(McqRun run, TextWriter w)
    {
        foreach (var sweep in run.Sweeps)
        {
            w.WriteLine();
            w.WriteLine($"Sweep · {sweep.Knob} (with-rag):");
            var best = sweep.Points.Count == 0 ? null : sweep.Points.MaxBy(p => p.Accuracy);
            foreach (var p in sweep.Points)
            {
                var marker = ReferenceEquals(p, best) ? "  ← best" : "";
                w.WriteLine($"  {sweep.Knob}={p.Value,-8} {Pct(p.Accuracy)}  ({p.Correct}/{p.Total}){marker}");
            }
        }
    }

    private static void RenderSanity(McqRun run, TextWriter w, McqConditionSummary? rag, McqConditionSummary? oracle)
    {
        var unparsed = run.Conditions.Sum(c => c.Unparsed);
        var notes = new List<string>();

        // The genuine "can't measure retrieval" signal: BOTH with-rag and oracle saturated, so there is
        // no spread left to attribute to retrieval. (Oracle alone at ~100% is EXPECTED and good — it
        // confirms the gold passages entail their keyed answers; the reference benchmark's oracle was
        // 99.3%. So don't flag oracle=100% on its own.)
        if (rag is not null && oracle is not null && rag.Accuracy >= 0.98 && oracle.Accuracy >= 0.98)
            notes.Add("with-rag ≈ oracle ≈ 100% — no spread left to measure retrieval; add harder/multi-hop questions.");
        if (unparsed > 0)
            notes.Add($"{unparsed} unparsed repl(y/ies) — scored wrong; read --transcript to confirm it's a format issue, not a knob.");

        if (notes.Count == 0)
            return;
        w.WriteLine();
        w.WriteLine("Sanity:");
        foreach (var n in notes)
            w.WriteLine($"  ! {n}");
    }

    public static void RenderTranscript(McqRun run, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("══════════════════════════ TRANSCRIPTS ══════════════════════════");
        foreach (var group in run.Items.GroupBy(i => i.Id))
        {
            var first = group.First();
            w.WriteLine();
            w.WriteLine($"── {first.Id} [{first.Topic}] · expected {first.Expected} ──");
            foreach (var o in group)
            {
                var verdict = o.Parsed == 0 ? "UNPARSED" : (o.Correct == o.Reps ? "✓" : o.Correct == 0 ? "✗" : $"{o.Correct}/{o.Reps}");
                var chose = o.LastChosen?.ToString() ?? "?";
                w.WriteLine($"  {o.Condition.Label(),-12} chose {chose}  {verdict}");
                if (!string.IsNullOrWhiteSpace(o.LastReply))
                    w.WriteLine($"      {Clip(o.LastReply!.ReplaceLineEndings(" "), 160)}");
            }
        }
    }

    private static McqConditionSummary? Find(McqRun run, McqCondition c) =>
        run.Conditions.FirstOrDefault(s => s.Condition == c);

    private static string Pct(double r) => r.ToString("0.0%", CultureInfo.InvariantCulture);
    private static string Num(double r) => r.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Pts(double delta) =>
        $"{(delta >= 0 ? "+" : "")}{(delta * 100).ToString("0.0", CultureInfo.InvariantCulture)} pts";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
