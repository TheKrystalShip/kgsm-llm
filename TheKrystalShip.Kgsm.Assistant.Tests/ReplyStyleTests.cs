using FluentAssertions;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// A style is presentation, so its parse fails OPEN — to the full written answer, which is readable on
/// every surface. Refusing a call over a misspelt style, or guessing that an unknown one meant voice,
/// would break a caller (or truncate an answer) over how the reply is laid out.
/// </summary>
public class ReplyStyleTests
{
    [Theory]
    [InlineData("voice")]
    [InlineData("VOICE")]
    [InlineData("  Voice  ")]
    public void The_spoken_style_is_recognised_however_it_is_cased(string wire) =>
        ReplyStyles.Parse(wire).Should().Be(ReplyStyle.Voice);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("vioce")]          // a typo reads as written, never as a guess at what was meant
    [InlineData("telegram-haiku")] // a style a newer caller knows about and this build does not
    public void Anything_else_reads_as_the_full_written_answer(string? wire) =>
        ReplyStyles.Parse(wire).Should().Be(ReplyStyle.Default);

    [Fact]
    public void A_style_survives_the_round_trip_a_caller_writes_it_for()
    {
        ReplyStyles.Parse(ReplyStyle.Voice.ToWire()).Should().Be(ReplyStyle.Voice);
        ReplyStyles.Parse(ReplyStyle.Default.ToWire()).Should().Be(ReplyStyle.Default);
    }
}
