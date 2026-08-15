using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Files;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Locks the anchored replacement: what it changes, and — the part that matters — everything it
/// refuses rather than approximating. A config file's untouched bytes surviving verbatim is the whole
/// property a staged write rests on.
/// </summary>
public class FileEditTests
{
    // A single-line settings tuple, the shape that makes a whole-file rewrite dangerous: one line,
    // many values, nothing for a line-oriented safety net to hold on to.
    private const string Tuple =
        "[/Script/Pal.PalGameWorldSettings]\n" +
        "OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ExpRate=1.000000," +
        "bIsMultiplay=False,bIsRandomizerPalLevelRandom=False,DeathPenalty=All)\n";

    [Fact]
    public void Apply_ReplacesTheAnchorAndNothingElse()
    {
        var result = FileEdit.Apply(Tuple, "Difficulty=None", "Difficulty=Difficulty_Hard");

        result.IsApplied.Should().BeTrue();
        result.Content.Should().Be(Tuple.Replace("Difficulty=None", "Difficulty=Difficulty_Hard"));

        // Every other setting is byte-identical — the reason the model never sends them.
        result.Content.Should().Contain("bIsMultiplay=False")
            .And.Contain("bIsRandomizerPalLevelRandom=False")
            .And.Contain("DeathPenalty=All")
            .And.Contain("DayTimeSpeedRate=1.000000");
    }

    [Fact]
    public void Apply_PreservesLengthArithmetic()
    {
        var result = FileEdit.Apply("a=1\nb=2\nc=3\n", "b=2", "b=20");

        result.Content.Should().Be("a=1\nb=20\nc=3\n");
        result.Matches.Should().Be(1);
    }

    [Fact]
    public void Apply_AnEmptyReplacementDeletesTheAnchor()
    {
        var result = FileEdit.Apply("keep=1\ndrop=2\nkeep=3\n", "drop=2\n", "");

        result.IsApplied.Should().BeTrue();
        result.Content.Should().Be("keep=1\nkeep=3\n");
    }

    [Fact]
    public void Apply_AddsALineByAnchoringOnANeighbour()
    {
        var result = FileEdit.Apply("a=1\nb=2\n", "b=2", "b=2\nc=3");

        result.Content.Should().Be("a=1\nb=2\nc=3\n");
    }

    [Fact]
    public void Apply_NoMatch_IsRefused()
    {
        // The measured failure mode in miniature: a plausible-looking key that is not the file's.
        var result = FileEdit.Apply(Tuple, "bIsMultipla=True", "bIsMultiplay=True");

        result.Outcome.Should().Be(FileEditOutcome.NoMatch);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_SeveralMatches_IsRefused()
    {
        var result = FileEdit.Apply("enabled=true\nother=1\nenabled=true\n", "enabled=true", "enabled=false");

        result.Outcome.Should().Be(FileEditOutcome.Ambiguous);
        result.Matches.Should().BeGreaterThan(1);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_EmptyAnchor_IsRefused()
    {
        var result = FileEdit.Apply(Tuple, "", "anything");

        result.Outcome.Should().Be(FileEditOutcome.NoAnchor);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_AnchorEqualToItsReplacement_IsRefused()
    {
        var result = FileEdit.Apply(Tuple, "Difficulty=None", "Difficulty=None");

        result.Outcome.Should().Be(FileEditOutcome.NoChange);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_MatchesOrdinally_NotByCase()
    {
        var result = FileEdit.Apply("Difficulty=None\n", "difficulty=none", "Difficulty=Hard");

        result.Outcome.Should().Be(FileEditOutcome.NoMatch);
    }

    [Fact]
    public void Apply_AnchorSpanningLines_IsReplacedWhole()
    {
        var result = FileEdit.Apply("x=1\ny=2\nz=3\n", "y=2\nz=3", "y=20\nz=30");

        result.Content.Should().Be("x=1\ny=20\nz=30\n");
    }

    [Fact]
    public void Apply_AnEmptyFile_MatchesNothing()
    {
        FileEdit.Apply("", "Difficulty=None", "Difficulty=Hard").Outcome.Should().Be(FileEditOutcome.NoMatch);
    }

    [Fact]
    public void Apply_OverlappingOccurrencesCountAsSeveral()
    {
        FileEdit.Apply("aaaa", "aa", "b").Outcome.Should().Be(FileEditOutcome.Ambiguous);
    }
}
