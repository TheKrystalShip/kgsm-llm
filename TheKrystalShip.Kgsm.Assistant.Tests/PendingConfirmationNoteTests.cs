using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The mirror of <see cref="UnbackedActionClaimTests"/>: a turn that staged something real must not
/// leave the user holding an unexplained confirmation prompt.
/// </summary>
public class PendingConfirmationNoteTests
{
    [Theory]
    [InlineData("I've staged a restart of Ketchup — awaiting your confirmation.")]
    [InlineData("This is proposed, not done; it won't run until you approve it.")]
    [InlineData("Once you confirm, the server picks it up on its next restart.")]
    [InlineData("I've proposed the change for your go-ahead.")]
    public void A_reply_that_already_says_something_is_pending_needs_no_note(string reply) =>
        PendingConfirmationNote.IsPresentIn(reply).Should().BeTrue();

    [Theory]
    // The live failure: the turn staged a write_file on its last iteration, so the loop's own
    // step-limit reply reached the user next to a Confirm button it never mentions.
    [InlineData("I wasn't able to finish that after a few steps — could you rephrase or break it down?")]
    [InlineData("The difficulty setting lives in PalWorldSettings.ini.")]
    [InlineData("")]
    public void A_reply_that_never_mentions_it_gets_one(string reply) =>
        PendingConfirmationNote.IsPresentIn(reply).Should().BeFalse();

    [Fact]
    public void The_note_counts_the_actions_without_restating_them()
    {
        PendingConfirmationNote.For(1).Should().Contain("one action").And.Contain("has not run yet");
        PendingConfirmationNote.For(3).Should().Contain("3 actions");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_spoken_note_carries_nothing_a_synthesiser_reads_out(int staged)
    {
        var spoken = PendingConfirmationNote.For(staged, ReplyStyle.Voice);

        spoken.Should().NotContain("*").And.NotContain("\n");
        spoken.Should().Contain("confirm");
    }

    [Fact]
    public void The_spoken_note_still_says_it_is_waiting_on_the_user()
    {
        // The whole reason a spoken reply may be asked for tersely: this sentence is written by code,
        // so no amount of brevity in the model's own words can drop it.
        var spoken = PendingConfirmationNote.For(1, ReplyStyle.Voice);

        PendingConfirmationNote.IsPresentIn(spoken).Should().BeTrue();
        spoken.Should().Contain("won't run until you confirm it");
    }
}
