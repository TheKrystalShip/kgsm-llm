using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Files;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Locks the key-addressed replacement: which value it changes, and what it refuses. Addressing a
/// setting by name means the caller never sees the text being replaced, so every way this could edit
/// something other than the setting it was given has to be closed here — the caller has no way to
/// notice afterwards.
/// </summary>
public class SettingEditTests
{
    // The shape that forces this editor to exist: one line carrying every setting the game has, where
    // reproducing the text around a value is not something a small model can be asked to do.
    private const string Packed =
        "[/Script/Pal.PalGameWorldSettings]\n" +
        "OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ExpRate=1.000000," +
        "Randomizer_Seed=\"\",bIsMultiplay=False,PalDamageRateAttack=1.000000,DeathPenalty=All)\n";

    // The other common shape: one setting per line, the format write_file already handles well.
    private const string Lines =
        "max-players=20\n" +
        "difficulty=easy\n" +
        "motd=A Minecraft Server\n";

    [Fact]
    public void Apply_ChangesOnlyTheNamedValue()
    {
        var result = SettingEdit.Apply(Packed, "DayTimeSpeedRate", "0.500000");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("1.000000");
        result.Content.Should().Be(Packed.Replace("DayTimeSpeedRate=1.000000", "DayTimeSpeedRate=0.500000"));
    }

    [Fact]
    public void Apply_LeavesEverySiblingSettingByteForByte()
    {
        var result = SettingEdit.Apply(Packed, "Difficulty", "Difficulty_Hard");

        result.IsApplied.Should().BeTrue();
        // The value that changed is the only difference; every other setting is still spelled exactly
        // as it was, which is the property the model cannot be trusted to preserve itself.
        result.Content!.Replace("Difficulty=Difficulty_Hard", "Difficulty=None").Should().Be(Packed);
    }

    [Fact]
    public void Apply_WorksOnALineOrientedFileToo()
    {
        var result = SettingEdit.Apply(Lines, "difficulty", "hard");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("easy");
        result.Content.Should().Be("max-players=20\ndifficulty=hard\nmotd=A Minecraft Server\n");
    }

    [Fact]
    public void Apply_StopsAValueAtTheEndOfItsLine()
    {
        // The value must not run past the newline and swallow the setting under it.
        var result = SettingEdit.Apply(Lines, "max-players", "40");

        result.Content.Should().Be("max-players=40\ndifficulty=easy\nmotd=A Minecraft Server\n");
    }

    [Fact]
    public void Apply_KeepsAValueThatContainsSpaces()
    {
        var result = SettingEdit.Apply(Lines, "motd", "Welcome, friends");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("A Minecraft Server");
        result.Content.Should().Contain("motd=Welcome, friends\n");
    }

    [Fact]
    public void Apply_DoesNotMatchAKeyThatMerelyEndsWithTheName()
    {
        // "Rate" lives inside ExpRate, DayTimeSpeedRate and PalDamageRateAttack. Matching it there
        // would edit a setting nobody named, under a name that looks right in the reply.
        var result = SettingEdit.Apply(Packed, "Rate", "2.0");

        result.Outcome.Should().Be(SettingEditOutcome.NoMatch);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_MatchesTheKeyCaseInsensitively()
    {
        // Games spell their own settings inconsistently across their documentation; the value stays
        // byte-exact either way.
        var result = SettingEdit.Apply(Packed, "difficulty", "Difficulty_Hard");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("None");
    }

    [Fact]
    public void Apply_KeepsACommaInsideAQuotedValue()
    {
        var quoted = "ServerName=\"Bob's, place\",Difficulty=None\n";

        var result = SettingEdit.Apply(quoted, "ServerName", "\"Ketchup\"");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("\"Bob's, place\"");
        result.Content.Should().Be("ServerName=\"Ketchup\",Difficulty=None\n");
    }

    [Fact]
    public void Apply_SetsAnEmptyQuotedValue()
    {
        var result = SettingEdit.Apply(Packed, "Randomizer_Seed", "\"12345\"");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("\"\"");
        result.Content.Should().Contain("Randomizer_Seed=\"12345\",");
    }

    [Fact]
    public void Apply_RefusesAKeyThatIsSetTwice()
    {
        var twice = "difficulty=easy\ndifficulty=hard\n";

        var result = SettingEdit.Apply(twice, "difficulty", "peaceful");

        result.Outcome.Should().Be(SettingEditOutcome.Ambiguous);
        result.Matches.Should().Be(2);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_RefusesAKeyThatIsNotThere()
    {
        var result = SettingEdit.Apply(Lines, "pvp", "true");

        result.Outcome.Should().Be(SettingEditOutcome.NoMatch);
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_RefusesAKeyThatIsMentionedButNeverAssigned()
    {
        // The word appears in a comment; nothing is assigned to it, so there is no value to change.
        var commented = "# difficulty is explained in the wiki\nmax-players=20\n";

        var result = SettingEdit.Apply(commented, "difficulty", "hard");

        result.Outcome.Should().Be(SettingEditOutcome.NoMatch);
    }

    [Fact]
    public void Apply_RefusesAWholeAssignmentPassedAsTheKey()
    {
        // A caller passing "difficulty=easy" meant the anchored editor. Accepting it here would make
        // the key mean two different things depending on what was in it.
        var result = SettingEdit.Apply(Lines, "difficulty=easy", "hard");

        result.Outcome.Should().Be(SettingEditOutcome.NoKey);
    }

    [Fact]
    public void Apply_RefusesAnEmptyKey()
    {
        SettingEdit.Apply(Lines, "  ", "hard").Outcome.Should().Be(SettingEditOutcome.NoKey);
    }

    [Fact]
    public void Apply_RefusesAValueThatIsAlreadySet()
    {
        var result = SettingEdit.Apply(Lines, "difficulty", "easy");

        result.Outcome.Should().Be(SettingEditOutcome.NoChange);
        result.PreviousValue.Should().Be("easy");
        result.Content.Should().BeNull();
    }

    [Fact]
    public void Apply_ToleratesSpacesAroundTheAssignment()
    {
        var spaced = "difficulty = easy\nmax-players = 20\n";

        var result = SettingEdit.Apply(spaced, "difficulty", "hard");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("easy");
        result.Content.Should().Be("difficulty = hard\nmax-players = 20\n");
    }

    [Fact]
    public void Apply_PreservesCarriageReturnsAroundTheValue()
    {
        // A Windows-authored config must not lose its line endings to an edit.
        var crlf = "difficulty=easy\r\nmax-players=20\r\n";

        var result = SettingEdit.Apply(crlf, "difficulty", "hard");

        result.Content.Should().Be("difficulty=hard\r\nmax-players=20\r\n");
    }

    [Fact]
    public void Apply_SetsAValueThatWasEmpty()
    {
        var blank = "motd=\nmax-players=20\n";

        var result = SettingEdit.Apply(blank, "motd", "Hello");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().BeEmpty();
        result.Content.Should().Be("motd=Hello\nmax-players=20\n");
    }

    [Fact]
    public void Apply_ClearsAValueToEmpty()
    {
        var result = SettingEdit.Apply(Lines, "motd", "");

        result.IsApplied.Should().BeTrue();
        result.Content.Should().Be("max-players=20\ndifficulty=easy\nmotd=\n");
    }

    [Fact]
    public void Apply_KeepsTheClosingParenthesisOfAPackedLine()
    {
        // The last value in the tuple ends at ")" rather than at a comma or a newline; consuming the
        // parenthesis would corrupt the line the game parses.
        var result = SettingEdit.Apply(Packed, "DeathPenalty", "None");

        result.IsApplied.Should().BeTrue();
        result.PreviousValue.Should().Be("All");
        result.Content.Should().Contain("DeathPenalty=None)\n");
    }
}
