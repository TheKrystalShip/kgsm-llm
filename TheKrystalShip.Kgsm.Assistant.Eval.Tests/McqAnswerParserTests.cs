using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// The answer parser is the seam between a free-text reply and a scored letter, so its tiers (explicit
/// "Answer: X" marker → "the answer is X" → a bare trailing letter) and its honest parse-FAILURE
/// (out-of-range or absent → not a guess) are pinned here. A regression that silently mis-parses would
/// quietly skew every accuracy number.
/// </summary>
public class McqAnswerParserTests
{
    [Theory]
    [InlineData("Answer: B", 'B')]
    [InlineData("answer: b", 'B')]                                   // case-insensitive
    [InlineData("Answer: (D)", 'D')]                                 // parenthesized
    [InlineData("**Answer:** A", 'A')]                               // markdown bold
    [InlineData("Answer - C", 'C')]                                  // dash delimiter
    [InlineData("The answer is B.", 'B')]                            // "is" form
    [InlineData("I'll reason a bit.\nThe choice is clearly C.\nAnswer: C", 'C')]  // reason then commit
    [InlineData("First I thought Answer: A, but actually Answer: D", 'D')]        // last marker wins
    public void Parses_the_committed_letter_from_a_marker(string reply, char expected)
    {
        AnswerParser.TryParse(reply, 4, out var letter).Should().BeTrue();
        letter.Should().Be(expected);
    }

    [Theory]
    [InlineData("B", 'B')]
    [InlineData("(C)", 'C')]
    [InlineData("D.", 'D')]
    [InlineData("reasoning across lines\n\nA", 'A')]   // lone letter on the last non-empty line
    public void Falls_back_to_a_bare_trailing_letter(string reply, char expected)
    {
        AnswerParser.TryParse(reply, 4, out var letter).Should().BeTrue();
        letter.Should().Be(expected);
    }

    [Theory]
    [InlineData("Answer: F")]                          // out of range for 4 choices
    [InlineData("It really depends on the context.")]  // prose, no marker, no lone letter
    [InlineData("this is a good option to consider")]  // a stray 'a'/'is' must NOT be mistaken for an answer
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_failure_rather_than_guessing(string reply)
    {
        AnswerParser.TryParse(reply, 4, out _).Should().BeFalse();
    }

    [Fact]
    public void Respects_the_choice_count_bound()
    {
        // 'E' is valid with 5 choices but out of range with 4.
        AnswerParser.TryParse("Answer: E", 5, out var ok).Should().BeTrue();
        ok.Should().Be('E');
        AnswerParser.TryParse("Answer: E", 4, out _).Should().BeFalse();
    }

    [Fact]
    public void A_null_reply_is_a_failure_not_an_exception()
    {
        AnswerParser.TryParse(null, 4, out _).Should().BeFalse();
    }
}
