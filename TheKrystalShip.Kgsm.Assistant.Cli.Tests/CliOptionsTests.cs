using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>The command-line surface: flag parsing, value forms, the positional prompt, and the
/// authority axis (D1: authorized by default, <c>--read-only</c> opts down → <c>canPerformActions</c>).</summary>
public class CliOptionsTests
{
    private static CliOptions Parse(params string[] args)
    {
        CliOptions.TryParse(args, out var options, out var error).Should().BeTrue(error ?? "");
        return options;
    }

    [Fact]
    public void PositionalTokens_JoinIntoOnePrompt() =>
        Parse("is", "terraria", "up?").Prompt.Should().Be("is terraria up?");

    [Fact]
    public void NoArgs_LeavesPromptNull() =>   // → REPL / stdin
        Parse().Prompt.Should().BeNull();

    [Fact]
    public void Default_IsAuthorized()   // D1: no flag → actions allowed (canPerformActions = !ReadOnly)
    {
        var options = Parse("anything");
        options.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void ReadOnly_OptsDown() =>
        Parse("--read-only", "list").ReadOnly.Should().BeTrue();

    [Fact]
    public void Verbose_NoColor_Help_Parse()
    {
        var options = Parse("--verbose", "--no-color", "--help", "x");
        options.Verbose.Should().BeTrue();
        options.NoColor.Should().BeTrue();
        options.Help.Should().BeTrue();
    }

    [Fact]
    public void Help_ShortForm() => Parse("-h").Help.Should().BeTrue();

    [Theory]
    [InlineData("--model", "gemma4:12b")]
    public void Model_SpaceForm(string flag, string value) =>
        Parse(flag, value, "x").Model.Should().Be(value);

    [Fact]
    public void Model_EqualsForm() => Parse("--model=gemma4:12b", "x").Model.Should().Be("gemma4:12b");

    [Fact]
    public void Config_SpaceForm() => Parse("--config", "/tmp/c.json").ConfigPath.Should().Be("/tmp/c.json");

    [Fact]
    public void Config_EqualsForm() => Parse("--config=/tmp/c.json").ConfigPath.Should().Be("/tmp/c.json");

    [Fact]
    public void UnknownFlag_IsAnError()
    {
        CliOptions.TryParse(new[] { "--bogus" }, out _, out var error).Should().BeFalse();
        error.Should().Contain("--bogus");
    }

    [Fact]
    public void ValueFlag_MissingValue_IsAnError()
    {
        CliOptions.TryParse(new[] { "--model" }, out _, out var error).Should().BeFalse();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void LoneDash_IsPositional_NotAFlag() =>   // a bare '-' is a prompt token, not an unknown flag
        Parse("-").Prompt.Should().Be("-");

    [Fact]
    public void FlagsAndPrompt_Mix()
    {
        var options = Parse("--read-only", "what", "is", "up");
        options.ReadOnly.Should().BeTrue();
        options.Prompt.Should().Be("what is up");
    }
}
