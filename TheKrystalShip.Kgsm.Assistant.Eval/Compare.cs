using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Diffs two result files into the only view that matters for tuning: what got better, what regressed,
/// and by how much. Shows the dimension roll-up shift and the per-check moves above a threshold —
/// because at small rep counts a single flip is just noise, not a regression.
/// </summary>
internal static class Compare
{
    // A check rate moving by less than this at typical rep counts (N=3 → one flip = 0.33) is noise.
    private const double Threshold = 0.34;

    public static int Run(string basePath, string headPath, TextWriter w)
    {
        EvalRun a, b;
        try
        {
            a = EvalRun.Load(basePath);
            b = EvalRun.Load(headPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"compare: {ex.Message}");
            return 1;
        }

        w.WriteLine();
        w.WriteLine("═══ compare ═══");
        w.WriteLine($"BASE  {a.Model,-14} sys {a.SystemPromptHash ?? "?",-9} corpus {a.CorpusVersion}  reps {a.Reps}   {a.FinishedAt:u}");
        w.WriteLine($"HEAD  {b.Model,-14} sys {b.SystemPromptHash ?? "?",-9} corpus {b.CorpusVersion}  reps {b.Reps}   {b.FinishedAt:u}");
        w.WriteLine();

        if (a.CorpusVersion != b.CorpusVersion)
            w.WriteLine($"⚠ corpus version differs ({a.CorpusVersion} vs {b.CorpusVersion}) — checks may not line up; treat deltas with care.");
        if (a.Model != b.Model)
            w.WriteLine($"ℹ different models ({a.Model} vs {b.Model}) — this is a model comparison, not a tuning delta.");
        if (a.Reps != b.Reps)
            w.WriteLine($"ℹ rep counts differ ({a.Reps} vs {b.Reps}) — small-N rates are noisier.");

        // Dimension roll-up shift.
        w.WriteLine();
        w.WriteLine("Dimension shifts:");
        var bDims = b.Summary.ToDictionary(s => s.Dimension);
        foreach (var da in a.Summary)
        {
            if (!bDims.TryGetValue(da.Dimension, out var db)) continue;
            if (!da.Covered && !db.Covered) continue;
            var arrow = Arrow(db.Rate - da.Rate);
            w.WriteLine($"  {Letter(da.Dimension)} {Name(da.Dimension),-16} {Rate(da.Rate)} → {Rate(db.Rate)}   {arrow}");
        }

        // Per-check moves above the noise threshold.
        var moves = new List<(string id, string label, string dim, double from, double to)>();
        var bCases = b.Cases.ToDictionary(c => c.Id);
        foreach (var ca in a.Cases.Where(c => !c.Skipped))
        {
            if (!bCases.TryGetValue(ca.Id, out var cb) || cb.Skipped) continue;
            var bChecks = cb.Checks.ToDictionary(x => x.Key);
            foreach (var xa in ca.Checks)
                if (bChecks.TryGetValue(xa.Key, out var xb) && Math.Abs(xb.Rate - xa.Rate) >= Threshold)
                    moves.Add((ca.Id, xa.Label, xa.Dimension, xa.Rate, xb.Rate));
        }

        var regressions = moves.Where(m => m.to < m.from).OrderBy(m => m.to - m.from).ToList();
        var improvements = moves.Where(m => m.to > m.from).OrderByDescending(m => m.to - m.from).ToList();

        w.WriteLine();
        w.WriteLine($"Per-check regressions (Δ ≤ -{Threshold:0.00}):");
        if (regressions.Count == 0) w.WriteLine("  none");
        foreach (var m in regressions)
            w.WriteLine($"  ▼ {m.id,-4} [{Letter(m.dim)}] {m.label}   {Rate(m.from)} → {Rate(m.to)}");

        w.WriteLine();
        w.WriteLine($"Per-check improvements (Δ ≥ +{Threshold:0.00}):");
        if (improvements.Count == 0) w.WriteLine("  none");
        foreach (var m in improvements)
            w.WriteLine($"  ▲ {m.id,-4} [{Letter(m.dim)}] {m.label}   {Rate(m.from)} → {Rate(m.to)}");

        w.WriteLine();
        w.WriteLine($"Overall: {Rate(a.OverallRate)} → {Rate(b.OverallRate)}   {Arrow(b.OverallRate - a.OverallRate)}");
        w.WriteLine($"Net: {improvements.Count} improved, {regressions.Count} regressed (threshold {Threshold:0.00}).");
        return 0;
    }

    private static string Arrow(double delta) => delta switch
    {
        > 0.001 => $"▲ +{delta.ToString("0.00", CultureInfo.InvariantCulture)}",
        < -0.001 => $"▼ {delta.ToString("0.00", CultureInfo.InvariantCulture)}",
        _ => "·  0.00",
    };

    private static string Letter(string dim) => dim.Split('_')[0];
    private static string Name(string dim) { var i = dim.IndexOf('_'); return i < 0 ? dim : dim[(i + 1)..]; }
    private static string Rate(double r) => r.ToString("0.00", CultureInfo.InvariantCulture);
}
