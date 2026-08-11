using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>Renders an <see cref="EvalRun"/> as a terminal scorecard: per-case dimension rates, a
/// dimension roll-up (with F honestly marked uncovered), and the weak checks worth reading.</summary>
internal static class Scorecard
{
    private static readonly string[] DimOrder = Enum.GetNames<Rubric>();

    public static void Render(EvalRun run, TextWriter w)
    {
        var seed = run.Seed?.ToString(CultureInfo.InvariantCulture) ?? "—";
        w.WriteLine();
        w.WriteLine($"═══ kgsm-assistant eval · {run.Model} · temp {run.Temperature.ToString(CultureInfo.InvariantCulture)} · " +
                    $"reps {run.Reps} · seed {seed} · corpus {run.CorpusVersion} · sys {run.SystemPromptHash ?? "?"} ═══");
        w.WriteLine($"host: {run.Host.Instances} instance(s) ({run.Host.Running} running, {run.Host.Stopped} stopped), {run.Host.Blueprints} blueprint(s)");
        w.WriteLine();

        // Header: CASE TITLE  A B C D E  checks
        var letters = DimOrder.Select(Letter).ToArray();
        w.WriteLine($"{"CASE",-5} {"TITLE",-38} {string.Join(" ", letters.Select(l => $"{l,4}"))}  checks");
        foreach (var c in run.Cases)
        {
            if (c.Skipped)
            {
                w.WriteLine($"{c.Id,-5} {Clip(c.Title, 38),-38}  SKIPPED — {c.SkipReason}");
                continue;
            }

            var cells = DimOrder.Select(d => c.Dimensions.TryGetValue(d, out var r) ? Rate(r) : "  -").ToArray();
            var passed = c.Checks.Sum(x => x.Passed);
            var total = c.Checks.Sum(x => x.Total);
            w.WriteLine($"{c.Id,-5} {Clip(c.Title, 38),-38} {string.Join(" ", cells.Select(x => $"{x,4}"))}  {passed}/{total}");
        }

        w.WriteLine();
        w.WriteLine("Dimensions (auto-scored across all cases × reps):");
        foreach (var d in run.Summary)
        {
            if (!d.Covered)
            {
                w.WriteLine($"  {Letter(d.Dimension)} {Name(d.Dimension),-16} —    not auto-scored (read transcripts to judge)");
                continue;
            }
            w.WriteLine($"  {Letter(d.Dimension)} {Name(d.Dimension),-16} {Rate(d.Rate)}   ({d.Passed}/{d.Total})");
        }

        var coveredPass = run.Summary.Where(s => s.Covered).Sum(s => s.Passed);
        var coveredTotal = run.Summary.Where(s => s.Covered).Sum(s => s.Total);
        w.WriteLine();
        w.WriteLine($"Overall: {Rate(run.OverallRate)}  ({coveredPass}/{coveredTotal} checks)");

        // Printed next to the score, not in a footnote: the number above is only a measurement of the
        // assistant for the turns that reached it, and a reader comparing two runs has no other way to
        // know part of one never did.
        if (run.Health is { TurnsErrored: > 0 } h)
        {
            w.WriteLine();
            if (h.Degraded)
            {
                w.WriteLine($"⚠  DEGRADED RUN — {h.TurnsErrored} of {h.TurnsRun} turns ({h.ErrorRate:P0}) failed outright.");
                w.WriteLine("   Those turns reached no model, so their checks failed for want of an answer");
                w.WriteLine("   rather than a wrong one. Do NOT read this score as the assistant's behaviour,");
                w.WriteLine("   and do not compare it against a clean run. Check the endpoint and run again.");
            }
            else
            {
                w.WriteLine($"note: {h.TurnsErrored} of {h.TurnsRun} turns errored ({h.ErrorRate:P1}) — "
                            + "their checks failed for want of a reply.");
            }
        }

        // The checks worth reading: anything that didn't pass every rep.
        var weak = run.Cases.Where(c => !c.Skipped)
            .SelectMany(c => c.Checks.Where(x => x.Rate < 1.0).Select(x => (c.Id, x)))
            .OrderBy(t => t.x.Rate)
            .ToList();
        if (weak.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Weak checks (< 100%) — where tuning has headroom:");
            foreach (var (id, x) in weak)
                w.WriteLine($"  {Rate(x.Rate)}  {id,-4} [{Letter(x.Dimension)}] {x.Label}  ({x.Passed}/{x.Total})");
        }
    }

    private static string Letter(string dim) => dim.Split('_')[0];

    private static string Name(string dim)
    {
        var i = dim.IndexOf('_');
        return i < 0 ? dim : dim[(i + 1)..];
    }

    private static string Rate(double r) => r.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
