using FluentAssertions;

using TheKrystalShip.Rag.Indexer;

namespace TheKrystalShip.Rag.Indexer.Tests;

public class IndexerArgsTests
{
    [Fact]
    public void Parses_a_minimal_once_invocation_with_defaults()
    {
        var ok = IndexerArgs.TryParse(["--once", "--source", "./docs", "--index", "out.bin"], out var a, out var error);

        ok.Should().BeTrue(because: error);
        a.Once.Should().BeTrue();
        a.Sources.Should().Equal("./docs");
        a.IndexPath.Should().Be("out.bin");
        a.Model.Should().Be("embeddinggemma");
        a.Endpoint.Should().Be("http://localhost:11434");
        a.Pattern.Should().Be("*.md");
        a.ChunkSize.Should().Be(2000);
    }

    [Fact]
    public void Accumulates_repeated_sources()
    {
        IndexerArgs.TryParse(["--once", "--source", "a", "--source", "b", "--index", "i"], out var a, out _)
            .Should().BeTrue();
        a.Sources.Should().Equal("a", "b");
    }

    [Fact]
    public void Accepts_the_inline_equals_form_including_integers()
    {
        IndexerArgs.TryParse(["--once", "--source=docs", "--index=out.bin", "--chunk-size=512"], out var a, out _)
            .Should().BeTrue();
        a.Sources.Should().Equal("docs");
        a.IndexPath.Should().Be("out.bin");
        a.ChunkSize.Should().Be(512);
    }

    [Fact]
    public void A_value_flag_without_a_value_is_an_error()
    {
        IndexerArgs.TryParse(["--once", "--index"], out _, out var error).Should().BeFalse();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void A_non_integer_for_an_int_flag_is_an_error()
    {
        IndexerArgs.TryParse(["--chunk-size", "lots"], out _, out var error).Should().BeFalse();
        error.Should().Contain("expects an integer");
    }

    [Fact]
    public void An_unknown_flag_is_an_error()
    {
        IndexerArgs.TryParse(["--once", "--frobnicate"], out _, out var error).Should().BeFalse();
        error.Should().Contain("unknown option");
    }

    [Fact]
    public void Watch_parses_so_the_host_can_message_it_as_unimplemented()
    {
        IndexerArgs.TryParse(["--watch", "--source", "docs", "--index", "i"], out var a, out _).Should().BeTrue();
        a.Watch.Should().BeTrue();
        a.Once.Should().BeFalse();
    }

    [Fact]
    public void Boolean_flags_are_recognised()
    {
        IndexerArgs.TryParse(["--once", "--full", "--verbose", "--source", "d", "--index", "i"], out var a, out _)
            .Should().BeTrue();
        a.Full.Should().BeTrue();
        a.Verbose.Should().BeTrue();
    }
}
