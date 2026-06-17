using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

public class EvalOptionsTests
{
    [Fact]
    public void Defaults_are_gemma_three_reps_unseeded()
    {
        EvalOptions.TryParse(Array.Empty<string>(), out var o, out var err).Should().BeTrue();
        err.Should().BeNull();
        o.Model.Should().Be("gemma4:12b");
        o.Reps.Should().Be(3);
        o.Seed.Should().BeNull();
        o.Temperature.Should().Be(0.3);
        o.IsCompare.Should().BeFalse();
    }

    [Fact]
    public void Parses_run_flags()
    {
        EvalOptions.TryParse(new[] { "--model", "qwen3.5:9b", "-n", "5", "--seed", "7", "--temp", "0.1", "--filter", "A1,C8,D" },
            out var o, out _).Should().BeTrue();
        o.Model.Should().Be("qwen3.5:9b");
        o.Reps.Should().Be(5);
        o.Seed.Should().Be(7);
        o.Temperature.Should().Be(0.1);
        o.Filter.Should().BeEquivalentTo("A1", "C8", "D");
    }

    [Fact]
    public void Compare_needs_exactly_two_paths()
    {
        EvalOptions.TryParse(new[] { "compare", "a.json", "b.json" }, out var o, out _).Should().BeTrue();
        o.IsCompare.Should().BeTrue();
        o.CompareBase.Should().Be("a.json");
        o.CompareHead.Should().Be("b.json");

        EvalOptions.TryParse(new[] { "compare", "only-one.json" }, out _, out var err).Should().BeFalse();
        err.Should().Contain("two result files");
    }

    [Theory]
    [InlineData("--reps", "0")]
    [InlineData("--reps", "abc")]
    [InlineData("--seed", "x")]
    [InlineData("--temp", "hot")]
    public void Rejects_bad_numeric_values(string flag, string value)
    {
        EvalOptions.TryParse(new[] { flag, value }, out _, out var err).Should().BeFalse();
        err.Should().NotBeNull();
    }

    [Fact]
    public void Unknown_option_errors()
    {
        EvalOptions.TryParse(new[] { "--nope" }, out _, out var err).Should().BeFalse();
        err.Should().Contain("unknown option");
    }

    [Fact]
    public void Missing_value_errors()
    {
        EvalOptions.TryParse(new[] { "--model" }, out _, out var err).Should().BeFalse();
        err.Should().Contain("expects a value");
    }
}
