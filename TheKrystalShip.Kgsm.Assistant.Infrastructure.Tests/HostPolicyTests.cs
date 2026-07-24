using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>Unit-tests the operator allow/deny overlay in isolation — NOT a safety boundary itself
/// (that's <see cref="SsrfGuard"/>), just the optional scoping knob on top of it.</summary>
public class HostPolicyTests
{
    [Fact]
    public void NoLists_NothingIsBlocked()
    {
        HostPolicy.IsBlocked("example.com", [], [], out var reason).Should().BeFalse();
        reason.Should().BeNull();
    }

    [Fact]
    public void Denylist_ExactMatch_IsBlocked()
    {
        HostPolicy.IsBlocked("evil.example.com", [], ["evil.example.com"], out var reason).Should().BeTrue();
        reason.Should().Contain("denylist");
    }

    [Fact]
    public void Denylist_SubdomainMatch_IsBlocked()
    {
        HostPolicy.IsBlocked("api.evil.com", [], ["evil.com"], out _).Should().BeTrue();
    }

    [Fact]
    public void Denylist_UnrelatedHost_IsNotBlocked()
    {
        HostPolicy.IsBlocked("notevil.com", [], ["evil.com"], out _).Should().BeFalse();
    }

    [Fact]
    public void Allowlist_NonMember_IsBlocked()
    {
        HostPolicy.IsBlocked("random.com", ["github.com"], [], out var reason).Should().BeTrue();
        reason.Should().Contain("allowlist");
    }

    [Fact]
    public void Allowlist_ExactAndSubdomainMembers_AreAllowed()
    {
        HostPolicy.IsBlocked("github.com", ["github.com"], [], out _).Should().BeFalse();
        HostPolicy.IsBlocked("raw.githubusercontent.com", ["githubusercontent.com"], [], out _).Should().BeFalse();
    }

    [Fact]
    public void Denylist_WinsOverAllowlist()
    {
        HostPolicy.IsBlocked("github.com", ["github.com"], ["github.com"], out var reason).Should().BeTrue();
        reason.Should().Contain("denylist");
    }
}
