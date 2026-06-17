using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

public class CompareTests
{
    // A minimal run: one case ("C8") with one propose-only check at the given pass-rate (over 3 reps).
    // Compare reads only the check summaries + dimension roll-up, so the reps array can be empty.
    private static EvalRun MiniRun(double rate, string sys)
    {
        var passed = (int)Math.Round(rate * 3);
        var check = new CheckSummaryDto("s0:stages Restart for confirmation", "stages Restart for confirmation", "C_ProposeOnly", passed, 3, rate);
        var c8 = new CaseResultDto("C8", "restart <game>", true, false, null,
            Array.Empty<RepResultDto>(), new[] { check }, new Dictionary<string, double> { ["C_ProposeOnly"] = rate });
        var dim = new DimensionSummaryDto("C_ProposeOnly", passed, 3, rate, Covered: true);
        return new EvalRun(EvalRun.CurrentSchema, "v1", default, default, "gemma4:12b", 0.3, null, 3, sys,
            new HostInfoDto(1, 1, 0, 2, new Dictionary<string, string?>()),
            new[] { c8 }, new[] { dim }, rate);
    }

    private static string Save(EvalRun run)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eval-cmp-{Guid.NewGuid():N}.json");
        run.Save(path);
        return path;
    }

    [Fact]
    public void Flags_a_regression_above_threshold()
    {
        var baseP = Save(MiniRun(1.0, "aaaa"));
        var headP = Save(MiniRun(0.0, "bbbb"));
        var w = new StringWriter();

        Compare.Run(baseP, headP, w).Should().Be(0);

        var output = w.ToString();
        output.Should().Contain("1 regressed");
        output.Should().MatchRegex(@"▼\s+C8");
        output.Should().Contain("none", "there should be no improvements section content");
    }

    [Fact]
    public void Flags_an_improvement_above_threshold()
    {
        var baseP = Save(MiniRun(0.0, "aaaa"));
        var headP = Save(MiniRun(1.0, "bbbb"));
        var w = new StringWriter();

        Compare.Run(baseP, headP, w);

        var output = w.ToString();
        output.Should().Contain("1 improved");
        output.Should().MatchRegex(@"▲\s+C8");
    }

    [Fact]
    public void Ignores_subthreshold_noise()
    {
        // 1.0 → 0.67 is a single flip at N=3 (0.33) — under the 0.34 threshold, so it's noise.
        var baseP = Save(MiniRun(1.0, "aaaa"));
        var headP = Save(MiniRun(0.67, "bbbb"));
        var w = new StringWriter();

        Compare.Run(baseP, headP, w);

        var output = w.ToString();
        output.Should().Contain("0 improved, 0 regressed");
    }

    [Fact]
    public void Warns_when_corpus_versions_differ()
    {
        var a = MiniRun(1.0, "aaaa");
        var b = a with { CorpusVersion = "v2" };
        var w = new StringWriter();

        Compare.Run(Save(a), Save(b), w);

        w.ToString().Should().Contain("corpus version differs");
    }
}
