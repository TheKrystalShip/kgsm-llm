using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

public class IndexVerbArgsTests
{
    [Fact]
    public void Defaults_are_empty_and_a_bare_verb_parses()
    {
        IndexVerbArgs.TryParse([], out var a, out var error).Should().BeTrue(because: error);
        a.Sources.Should().BeEmpty();
        a.Full.Should().BeFalse();
    }

    [Fact]
    public void Collects_sources_and_flags()
    {
        IndexVerbArgs.TryParse(["--source", "docs", "--source=more", "--full", "--verbose"], out var a, out _)
            .Should().BeTrue();
        a.Sources.Should().Equal("docs", "more");
        a.Full.Should().BeTrue();
        a.Verbose.Should().BeTrue();
    }

    [Fact]
    public void Config_flag_and_its_value_are_accepted_and_skipped()
    {
        // --config is consumed during config resolution; the verb parser must not reject it.
        IndexVerbArgs.TryParse(["--config", "/etc/kgsm.json", "--full"], out var a, out var error)
            .Should().BeTrue(because: error);
        a.Full.Should().BeTrue();
        IndexVerbArgs.TryParse(["--config=/etc/kgsm.json"], out _, out _).Should().BeTrue();
    }

    [Fact]
    public void A_source_without_a_value_is_an_error()
    {
        IndexVerbArgs.TryParse(["--source"], out _, out var error).Should().BeFalse();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void An_unknown_flag_is_an_error()
    {
        IndexVerbArgs.TryParse(["--wat"], out _, out var error).Should().BeFalse();
        error.Should().Contain("unknown option");
    }
}
