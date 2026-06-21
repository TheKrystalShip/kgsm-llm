using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>The deterministic core of the Phase 6 retrieval read: lexical gold-coverage, the diagnostic
/// roll-ups, and the a/b/c failure-mode classifier that gates the lever decision. No model, no index.</summary>
public class McqDiagnosisTests
{
    // ── TextOverlap.Coverage ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Coverage_is_one_when_every_gold_content_token_is_present()
    {
        var gold = "the watchdog owns the per-instance cgroups";
        var text = "On this host the watchdog owns the per-instance cgroups and restarts crashed servers.";
        TextOverlap.Coverage(gold, text).Should().Be(1.0);
    }

    [Fact]
    public void Coverage_is_zero_for_disjoint_text()
    {
        TextOverlap.Coverage("watchdog cgroups lifecycle", "completely unrelated kitchen recipe instructions")
            .Should().Be(0.0);
    }

    [Fact]
    public void Coverage_is_the_fraction_of_distinct_content_tokens_covered()
    {
        // gold content tokens: {watchdog, cgroups, restart, native} (stop/short words dropped)
        // text contains watchdog + cgroups only → 2/4.
        var gold = "watchdog cgroups restart native";
        var text = "the watchdog manages cgroups";
        TextOverlap.Coverage(gold, text).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Coverage_ignores_stopwords_and_short_tokens()
    {
        // gold reduces to {metrics} only — "the/and/of" are stop, "is/a" are < 3 chars.
        var gold = "the metrics and a of is";
        TextOverlap.Coverage(gold, "metrics dashboard").Should().Be(1.0);
        TextOverlap.Coverage(gold, "the and of a is").Should().Be(0.0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a of to")] // all stop/short → no content tokens
    public void Coverage_is_zero_when_gold_has_no_content_tokens(string? gold)
    {
        TextOverlap.Coverage(gold, "anything at all here").Should().Be(0.0);
    }

    // ── RetrievalDiagnostic roll-ups ──────────────────────────────────────────────────────────────

    private static RetrievedHit Hit(double score, double gold, bool survived, bool rightDoc = false) =>
        new("doc.md", "H", score, gold, survived, rightDoc);

    [Fact]
    public void Diagnostic_rollups_take_the_best_and_count_survivors()
    {
        var d = new RetrievalDiagnostic(new[]
        {
            Hit(0.61, gold: 0.9, survived: true,  rightDoc: true),
            Hit(0.40, gold: 0.2, survived: true),
            Hit(0.30, gold: 0.95, survived: false), // best gold overlap, but did NOT reach context
        });

        d.TopScore.Should().BeApproximately(0.61, 1e-9);
        d.BestGoldOverlapTopK.Should().BeApproximately(0.95, 1e-9);   // includes the dropped chunk
        d.BestGoldOverlapContext.Should().BeApproximately(0.9, 1e-9); // only survivors
        d.SurvivedCount.Should().Be(2);
        d.RightDocInTopK.Should().BeTrue();
    }

    [Fact]
    public void Empty_diagnostic_does_not_throw_and_reads_zero()
    {
        var d = new RetrievalDiagnostic(Array.Empty<RetrievedHit>());
        d.TopScore.Should().Be(0);
        d.BestGoldOverlapTopK.Should().Be(0);
        d.BestGoldOverlapContext.Should().Be(0);
        d.SurvivedCount.Should().Be(0);
        d.RightDocInTopK.Should().BeFalse();
    }

    // ── RetrievalDiagnosis.Classify (the a/b/c gate) ──────────────────────────────────────────────

    [Fact]
    public void Classify_a_gold_missed_topk_when_no_chunk_covers_the_gold()
    {
        var d = new RetrievalDiagnostic(new[] { Hit(0.5, gold: 0.2, survived: true), Hit(0.4, gold: 0.1, survived: true) });
        RetrievalDiagnosis.Classify(d).Should().Be(RetrievalBucket.GoldMissedTopK);
    }

    [Fact]
    public void Classify_b_dropped_when_gold_is_in_topk_but_not_in_context()
    {
        // A high-overlap chunk exists in the raw top-k but was dropped by the context cap (survived=false).
        var d = new RetrievalDiagnostic(new[]
        {
            Hit(0.6, gold: 0.1, survived: true),
            Hit(0.3, gold: 0.9, survived: false),
        });
        RetrievalDiagnosis.Classify(d).Should().Be(RetrievalBucket.GoldDroppedFromContext);
    }

    [Fact]
    public void Classify_c_model_ceiling_when_gold_survived_into_context()
    {
        var d = new RetrievalDiagnostic(new[] { Hit(0.6, gold: 0.9, survived: true) });
        RetrievalDiagnosis.Classify(d).Should().Be(RetrievalBucket.GoldInContext);
    }

    [Fact]
    public void Classify_respects_an_explicit_threshold()
    {
        var d = new RetrievalDiagnostic(new[] { Hit(0.6, gold: 0.45, survived: true) });
        RetrievalDiagnosis.Classify(d, threshold: 0.5).Should().Be(RetrievalBucket.GoldMissedTopK);
        RetrievalDiagnosis.Classify(d, threshold: 0.4).Should().Be(RetrievalBucket.GoldInContext);
    }
}
