using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>The small deterministic value types behind the scorecard: accuracy/parse math, the item
/// helpers (letter mapping, choice formatting), and condition-token parsing.</summary>
public class McqScoringTests
{
    [Fact]
    public void Condition_summary_computes_accuracy_and_parse_rate()
    {
        var s = new McqConditionSummary(McqCondition.WithRag, Correct: 24, Parsed: 30, Total: 30);
        s.Accuracy.Should().BeApproximately(0.8, 1e-9);
        s.ParseRate.Should().Be(1.0);
        s.Unparsed.Should().Be(0);
    }

    [Fact]
    public void Unparsed_is_total_minus_parsed()
    {
        var s = new McqConditionSummary(McqCondition.ClosedBook, Correct: 5, Parsed: 8, Total: 10);
        s.Unparsed.Should().Be(2);
        s.Accuracy.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Empty_total_does_not_divide_by_zero()
    {
        var s = new McqConditionSummary(McqCondition.Oracle, 0, 0, 0);
        s.Accuracy.Should().Be(0);
        s.ParseRate.Should().Be(0);
    }

    [Theory]
    [InlineData("closed-book", "ClosedBook")]
    [InlineData("closed", "ClosedBook")]
    [InlineData("no-rag", "ClosedBook")]
    [InlineData("with-rag", "WithRag")]
    [InlineData("rag", "WithRag")]
    [InlineData("oracle", "Oracle")]
    [InlineData("gold", "Oracle")]
    public void Parses_condition_tokens(string token, string expected)
    {
        // McqCondition is internal, so compare by name rather than putting the enum in a public signature.
        McqConditions.TryParse(token, out var c).Should().BeTrue();
        c.ToString().Should().Be(expected);
    }

    [Fact]
    public void Rejects_an_unknown_condition_token()
    {
        McqConditions.TryParse("sideways", out _).Should().BeFalse();
    }

    [Fact]
    public void Item_maps_the_answer_letter_and_formats_choices()
    {
        var item = new McqItem("Q", "t", "what?", new[] { "alpha", "beta", "gamma", "delta" }, "C", "src.md", "gold");
        item.AnswerLetter.Should().Be('C');

        var formatted = item.FormatChoices();
        formatted.Should().Contain("A. alpha");
        formatted.Should().Contain("C. gamma");
        formatted.Should().Contain("D. delta");
    }

    [Fact]
    public void Sweep_point_computes_accuracy()
    {
        new McqSweepPoint("5", Correct: 3, Parsed: 4, Total: 4).Accuracy.Should().BeApproximately(0.75, 1e-9);
    }
}
