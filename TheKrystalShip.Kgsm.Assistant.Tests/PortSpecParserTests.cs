using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Network;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The <c>open_ports</c> spec parser is the single home for turning the model's free-text ports into
/// validated rules and back to the canonical string carried on the confirmation token. These pin the
/// accepted forms, the no-protocol → both-protocols expansion, range/round-trip fidelity, and that a
/// malformed / out-of-range spec is rejected with a message (never silently staged).
/// </summary>
public class PortSpecParserTests
{
    [Fact]
    public void SinglePortWithProtocol_ParsesOne()
    {
        PortSpecParser.TryParse("34197/udp", out var rules, out var error).Should().BeTrue();
        error.Should().BeNull();
        rules.Should().ContainSingle();
        rules[0].Should().Be(new PortRule(34197, 34197, "udp"));
    }

    [Fact]
    public void NoProtocol_ExpandsToBothTcpAndUdp()
    {
        PortSpecParser.TryParse("27015", out var rules, out _).Should().BeTrue();
        rules.Should().HaveCount(2);
        rules.Should().Contain(new PortRule(27015, 27015, "tcp"));
        rules.Should().Contain(new PortRule(27015, 27015, "udp"));
    }

    [Fact]
    public void Range_PreservesStartAndEnd()
    {
        PortSpecParser.TryParse("27015:27020/tcp", out var rules, out _).Should().BeTrue();
        rules.Should().ContainSingle();
        rules[0].Should().Be(new PortRule(27015, 27020, "tcp"));
    }

    [Fact]
    public void MultipleEntries_CommaAndPipeSeparated_Dedup()
    {
        PortSpecParser.TryParse("34197/udp, 34197/udp | 27015/tcp", out var rules, out _).Should().BeTrue();
        rules.Should().HaveCount(2); // the duplicate 34197/udp collapses
    }

    [Fact]
    public void Canonical_RoundTrips()
    {
        PortSpecParser.TryParse("27015:27020/tcp,34197/udp", out var rules, out _).Should().BeTrue();
        var canonical = PortSpecParser.ToCanonical(rules);
        PortSpecParser.TryParse(canonical, out var again, out _).Should().BeTrue();
        again.Should().BeEquivalentTo(rules);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("70000/tcp")]     // out of range
    [InlineData("0/udp")]         // out of range
    [InlineData("27020:27015/tcp")] // inverted range
    [InlineData("80/sctp")]       // unsupported protocol
    public void Invalid_IsRejected_WithMessage(string? spec)
    {
        PortSpecParser.TryParse(spec, out var rules, out var error).Should().BeFalse();
        rules.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
