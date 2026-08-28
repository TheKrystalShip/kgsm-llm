namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Prints each case's full conversation — prompt → tool trajectory → staged ops → reply — for
/// reading and judging by eye (or by a model). This is the "run it and evaluate live" surface: the
/// auto-checks are a guide, but the transcript is the ground truth a human reads to decide whether
/// the assistant actually behaved well. Enabled by <c>--transcript</c>.
/// </summary>
internal static class Transcripts
{
    public static void Render(EvalRun run, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine($"═══ transcripts · {run.Model} · sys {run.SystemPromptHash ?? "?"} ═══");

        foreach (var c in run.Cases)
        {
            if (c.Skipped)
            {
                w.WriteLine();
                w.WriteLine($"── {c.Id}  {c.Title}  — SKIPPED ({c.SkipReason})");
                continue;
            }

            w.WriteLine();
            w.WriteLine($"── {c.Id}  {c.Title}");
            foreach (var rep in c.Reps)
            {
                if (run.Reps > 1) w.WriteLine($"  rep {rep.Rep}:");
                var indent = run.Reps > 1 ? "    " : "  ";
                foreach (var s in rep.Steps)
                {
                    w.WriteLine($"{indent}user> {s.Prompt}");
                    foreach (var t in s.Tools) w.WriteLine($"{indent}  - {t}");
                    foreach (var st in s.Staged) w.WriteLine($"{indent}  - staged {st}");
                    foreach (var line in s.Final.Split('\n'))
                        w.WriteLine($"{indent}bot> {line}");
                    var failed = s.Checks.Where(x => !x.Pass).Select(x => $"[{x.Dimension.Split('_')[0]}] {x.Label}").ToList();
                    if (failed.Count > 0)
                        w.WriteLine($"{indent}  ✗ {string.Join("; ", failed)}");
                }
            }
        }
        w.WriteLine();
    }
}
