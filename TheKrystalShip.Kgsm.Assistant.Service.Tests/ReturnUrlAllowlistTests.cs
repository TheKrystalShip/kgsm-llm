using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The allowlist behind <c>return_to</c>. A redirect target that arrives on a request is an
/// open-redirect surface, and here it is one that hands over a real session — so what this admits
/// and what it refuses is the whole of the protection.
/// </summary>
public class ReturnUrlAllowlistTests
{
    private static AuthOptions With(params string[] origins) => new() { AllowedOrigins = origins };

    [Fact]
    public void AllowsAListedOrigin_AndKeepsThePathAndQuery()
    {
        // The origin is what is checked; where a client wants to land inside its own site is its business.
        With("https://panel.example.com")
            .TryResolveReturnUrl("https://panel.example.com/chat?tab=1", out var resolved)
            .Should().BeTrue();
        resolved.Should().Be("https://panel.example.com/chat?tab=1");
    }

    [Fact]
    public void DropsAnyFragmentTheCallerSupplied()
    {
        // The fragment is what carries the session back, so a caller-supplied one is overwritten
        // anyway — keeping half of it would only produce a confusing hybrid.
        With("https://panel.example.com")
            .TryResolveReturnUrl("https://panel.example.com/chat#already=here", out var resolved)
            .Should().BeTrue();
        resolved.Should().Be("https://panel.example.com/chat");
    }

    [Theory]
    [InlineData("https://evil.example.com")]                    // simply not listed
    [InlineData("https://panel.example.com.evil.com")]          // suffix that merely looks like it
    [InlineData("https://evil.com/?x=https://panel.example.com")] // the allowed origin as a payload, not the target
    [InlineData("http://panel.example.com")]                    // right host, wrong scheme
    [InlineData("https://panel.example.com:8443")]              // right host, a port nobody listed
    public void RefusesAnythingNotExactlyAListedOrigin(string candidate)
    {
        With("https://panel.example.com").TryResolveReturnUrl(candidate, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/chat")]                                   // relative: no origin to check
    [InlineData("//panel.example.com/chat")]                // protocol-relative: likewise
    [InlineData("javascript:alert(1)")]                     // not a navigable http(s) target
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void RefusesAnythingThatIsNotAnAbsoluteHttpUrl(string? candidate)
    {
        With("https://panel.example.com").TryResolveReturnUrl(candidate, out _).Should().BeFalse();
    }

    [Fact]
    public void RefusesEverythingWhenNothingIsListed()
    {
        // The shipped default is an empty list, so a host that never configured one hands nothing back.
        With().TryResolveReturnUrl("https://panel.example.com", out _).Should().BeFalse();
    }

    [Fact]
    public void ToleratesATrailingSlashInTheConfiguredOrigin()
    {
        // The option documents "no trailing slash", but a hand-edited env file is where this lands and
        // a stray slash should not silently disable a client's sign-in.
        With("https://panel.example.com/")
            .TryResolveReturnUrl("https://panel.example.com/chat", out var resolved)
            .Should().BeTrue();
        resolved.Should().Be("https://panel.example.com/chat");
    }

    [Fact]
    public void MatchesTheOriginCaseInsensitively()
    {
        With("https://Panel.Example.com")
            .TryResolveReturnUrl("https://panel.example.com/chat", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void AllowsAnyOfSeveralListedOrigins()
    {
        var options = With("https://a.example.com", "https://b.example.com");
        options.TryResolveReturnUrl("https://b.example.com/x", out var resolved).Should().BeTrue();
        resolved.Should().Be("https://b.example.com/x");
    }
}
