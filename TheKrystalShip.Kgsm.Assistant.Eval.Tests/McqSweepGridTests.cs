using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// The sweep grid resolves a knob name to a value list + an apply function that changes ONLY that knob.
/// A bug that mutated a second knob, or that didn't recognise an alias, would make a sweep's accuracy
/// movements un-attributable — so the single-axis guarantee is pinned here.
/// </summary>
public class McqSweepGridTests
{
    [Theory]
    [InlineData("topk", "top-k")]
    [InlineData("k", "top-k")]
    [InlineData("chunk", "chunk-size")]
    [InlineData("size", "chunk-size")]
    [InlineData("overlap", "chunk-overlap")]
    [InlineData("min", "min-score")]
    [InlineData("local", "local-min-score")]
    [InlineData("context", "max-context")]
    public void Canonicalizes_knob_aliases(string alias, string canonical)
    {
        SweepGrid.Canonicalize(alias).Should().Be(canonical);
    }

    [Fact]
    public void Resolving_top_k_varies_only_top_k()
    {
        var baseline = RagTuning.Default;
        var (canonical, values, apply) = SweepGrid.Resolve("topk", baseline);

        canonical.Should().Be("top-k");
        values.Should().NotBeEmpty();

        var tuned = apply("3");
        tuned.TopK.Should().Be(3);
        // every other knob untouched
        tuned.ChunkSize.Should().Be(baseline.ChunkSize);
        tuned.ChunkOverlap.Should().Be(baseline.ChunkOverlap);
        tuned.MinScore.Should().Be(baseline.MinScore);
        tuned.LocalMinScore.Should().Be(baseline.LocalMinScore);
        tuned.MaxContextChars.Should().Be(baseline.MaxContextChars);
    }

    [Fact]
    public void Resolving_min_score_parses_a_double()
    {
        var (_, _, apply) = SweepGrid.Resolve("min-score", RagTuning.Default);
        apply("0.5").MinScore.Should().Be(0.5);
    }

    [Fact]
    public void An_unknown_knob_is_a_clear_error()
    {
        var act = () => SweepGrid.Resolve("nonsense", RagTuning.Default);
        act.Should().Throw<McqRunException>().WithMessage("*unknown --sweep knob*");
    }
}
