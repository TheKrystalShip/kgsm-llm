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
                if (o.Retrieval is not null)
                    RenderProvenance(o.Retrieval, w);
                if (!string.IsNullOrWhiteSpace(o.LastReply))
                    w.WriteLine($"      {Clip(o.LastReply!.ReplaceLineEndings(" "), 160)}");
            }
        }
    }

    /// <summary>The retrieval read for one with-rag item: the headline gold-coverage numbers and the raw
    /// top-k it pulled — survived marker (✓ in context / · dropped), gold's-own-doc star, cosine, and how
    /// much of the gold passage each chunk lexically covers.</summary>
    private static void RenderProvenance(RetrievalDiagnostic d, TextWriter w)
    {
        w.WriteLine($"      retr: gold top-k={Num(d.BestGoldOverlapTopK)} ctx={Num(d.BestGoldOverlapContext)} · " +
                    $"right-doc={(d.RightDocInTopK ? "yes" : "no")} · survived {d.SurvivedCount}/{d.Hits.Count} · " +
                    $"top-score={Num(d.TopScore)}");
        for (var i = 0; i < d.Hits.Count; i++)
        {
            var h = d.Hits[i];
            var surv = h.Survived ? "✓" : "·";
            var doc = h.RightDoc ? "★" : " ";
            w.WriteLine($"        {i + 1}.[{surv}]{doc} {h.Score,5:0.00} gold={Num(h.GoldOverlap)}  " +
                        Clip(Path.GetFileName(h.SourcePath), 28));
        }
    }

    /// <summary>
    /// The Phase 6 retrieval read: recall@k across ALL with-rag questions (the powered metric — many
    /// points, not the handful of accuracy-gap questions), then the with-rag→oracle gap bucketed into the
    /// three failure modes that each imply a DIFFERENT lever (recall → hybrid; context/rank → budget/re-rank;
    /// model ceiling → no retrieval lever helps). The verdict line names the lever the data points to —
    /// the go/no-go gate before any retriever change is built.
    /// </summary>
    public static void RenderDiagnosis(McqRun run, TextWriter w)
    {
        var rag = run.Items.Where(i => i.Condition == McqCondition.WithRag).ToList();
        var withDiag = rag.Where(i => i.Retrieval is not null).ToList();

        w.WriteLine();
        w.WriteLine("═══ Retrieval diagnosis (with-rag) ═══");
        if (withDiag.Count == 0)
        {
            w.WriteLine("  (no with-rag retrieval captured — include with-rag in --conditions to diagnose retrieval.)");
            return;
        }

        var thr = RetrievalDiagnosis.GoldThreshold;
        var k = run.Tuning.TopK;
        var n = withDiag.Count;
        var goldInTopK = withDiag.Count(i => i.Retrieval!.BestGoldOverlapTopK >= thr);
        var goldInCtx = withDiag.Count(i => i.Retrieval!.BestGoldOverlapContext >= thr);
        var rightDoc = withDiag.Count(i => i.Retrieval!.RightDocInTopK);

        w.WriteLine($"  [gold \"retrieved\" = a chunk covering ≥ {Num(thr)} of the gold passage's content tokens]");
        w.WriteLine($"  recall@{k} (gold in raw top-k):  {Frac(goldInTopK, n)}");
        w.WriteLine($"  gold reached context:          {Frac(goldInCtx, n)}  (survived the MaxContextChars cap)");
        w.WriteLine($"  right doc retrieved (any chunk): {Frac(rightDoc, n)}");

        var oracleById = run.Items
            .Where(i => i.Condition == McqCondition.Oracle)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);
        if (oracleById.Count == 0)
        {
            w.WriteLine();
            w.WriteLine("  (no oracle condition — can't bucket the with-rag→oracle gap; add oracle to --conditions.)");
            return;
        }

        // The gap subset: oracle got it right (so the answer IS reachable from the docs) but with-rag did
        // not. Only a PARSED-but-wrong with-rag answer is retrieval-attributable; a with-rag reply that never
        // parsed (a model error/timeout) is a measurement defect, not a retrieval signal, so it's split out
        // as "inconclusive" rather than counted as a recall miss. Intended at reps 1; degrades sensibly above.
        var gaps = new List<(McqItemOutcome Rag, RetrievalBucket Bucket)>();
        var inconclusive = new List<McqItemOutcome>();
        foreach (var r in rag)
        {
            if (r.Retrieval is null || !oracleById.TryGetValue(r.Id, out var o))
                continue;
            var oracleRight = o.Reps > 0 && o.Correct == o.Reps;
            var ragWrong = r.Correct < r.Reps;
            if (!oracleRight || !ragWrong)
                continue;
            if (r.Parsed < r.Reps)
                inconclusive.Add(r);                                   // model error/timeout — not retrieval
            else
                gaps.Add((r, RetrievalDiagnosis.Classify(r.Retrieval)));
        }

        var a = gaps.Count(g => g.Bucket == RetrievalBucket.GoldMissedTopK);
        var b = gaps.Count(g => g.Bucket == RetrievalBucket.GoldDroppedFromContext);
        var c = gaps.Count(g => g.Bucket == RetrievalBucket.GoldInContext);

        w.WriteLine();
        w.WriteLine($"  with-rag → oracle gap — {gaps.Count} attributable question(s) (oracle correct, with-rag answered but wrong):");
        w.WriteLine($"    (a) gold missed top-k          {a}  → recall failure → BM25 hybrid / larger k");
        w.WriteLine($"    (b) gold dropped from context  {b}  → top-k / context-budget / re-rank");
        w.WriteLine($"    (c) gold in context, wrong     {c}  → model ceiling (no retrieval lever helps)");

        foreach (var (r, bucket) in gaps.OrderBy(g => g.Rag.Id, StringComparer.Ordinal))
        {
            var d = r.Retrieval!;
            w.WriteLine($"      {r.Id,-10} [{Clip(r.Topic, 12),-12}] {bucket,-22} " +
                        $"topk={Num(d.BestGoldOverlapTopK)} ctx={Num(d.BestGoldOverlapContext)} " +
                        $"rightDoc={(d.RightDocInTopK ? "yes" : "no ")} topScore={Num(d.TopScore)}");
        }

        if (inconclusive.Count > 0)
        {
            var ids = string.Join(", ", inconclusive.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id));
            w.WriteLine($"    inconclusive: {inconclusive.Count} question(s) gave no parsed with-rag answer " +
                        $"(model error/timeout, NOT a retrieval signal) — {ids}; read --transcript / raise the LLM timeout.");
        }

        w.WriteLine();
        w.WriteLine($"  → {Verdict(gaps.Count, a, b, c)}");
    }

    /// <summary>
    /// Fewer attributable gap questions than this read as noise: too few to attribute to retrieval or to
    /// validate a lever against (the advisor's "can't drive a lever off two data points"). The verdict
    /// refuses to recommend building a retriever below it.
    /// </summary>
    private const int MinActionableGap = 3;

    private static string Verdict(int gap, int a, int b, int c)
    {
        if (gap == 0)
            return "no measurable with-rag→oracle gap on this corpus — retrieval is already at the oracle ceiling.";
        if (gap < MinActionableGap)
            return $"WITHIN NOISE: only {gap} retrieval-attributable question(s) — too few to attribute or to validate a " +
                   "lever against. Don't build a retriever off this; expand the corpus to re-power, or accept the ceiling.";
        if (a >= b && a >= c)
            return "RECALL-BOUND: the gold passage is missing from top-k — a BM25+vector hybrid / larger k is the indicated lever.";
        if (b >= a && b >= c)
            return "CONTEXT/RANK-BOUND: gold is retrieved but dropped — raise MaxContextChars or re-rank before adding a hybrid.";
        return "MODEL-BOUND: gold is in context yet answered wrong — the gap is the model, not retrieval; a retrieval lever won't help.";
    }

    private static string Frac(int x, int n) =>
        $"{x}/{n} = {(n == 0 ? 0 : (double)x / n).ToString("0.0%", CultureInfo.InvariantCulture)}";

    private static McqConditionSummary? Find(McqRun run, McqCondition c) =>
        run.Conditions.FirstOrDefault(s => s.Condition == c);

    private static string Pct(double r) => r.ToString("0.0%", CultureInfo.InvariantCulture);
    private static string Num(double r) => r.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Pts(double delta) =>
        $"{(delta >= 0 ? "+" : "")}{(delta * 100).ToString("0.0", CultureInfo.InvariantCulture)} pts";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
