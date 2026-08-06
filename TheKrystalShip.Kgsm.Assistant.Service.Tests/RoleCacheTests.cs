using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class RoleCacheTests
{
    private static RoleCache Create(int ttl = 60) =>
        new(Options.Create(new AuthOptions { RoleCacheTtlSeconds = ttl }));

    [Fact]
    public void Set_Then_TryGet_HitsWithinTtl()
    {
        var cache = Create();
        cache.Set("u1", KgsmTier.Operator);

        cache.TryGet("u1", out var tier).Should().BeTrue();
        tier.Should().Be(KgsmTier.Operator);
    }

    [Fact]
    public void TryGet_Unknown_Misses()
    {
        Create().TryGet("nobody", out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_Evicts()
    {
        var cache = Create();
        cache.Set("u1", KgsmTier.Admin);
        cache.Remove("u1");

        cache.TryGet("u1", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredEntry_Misses()
    {
        var cache = Create(ttl: 1);
        cache.Set("u1", KgsmTier.Operator);

        await Task.Delay(1300);

        cache.TryGet("u1", out _).Should().BeFalse();
    }

    [Fact]
    public void OneUsersTier_IsNeverServedForAnother()
    {
        var cache = Create();
        cache.Set("u1", KgsmTier.Admin);
        cache.Set("u2", KgsmTier.Viewer);

        cache.TryGet("u1", out var first).Should().BeTrue();
        cache.TryGet("u2", out var second).Should().BeTrue();
        first.Should().Be(KgsmTier.Admin);
        second.Should().Be(KgsmTier.Viewer);
    }

    [Fact]
    public void Remove_LeavesOtherUsersUntouched()
    {
        // Logout drops one user entirely; everyone else keeps the answer they already paid for.
        var cache = Create();
        cache.Set("u1", KgsmTier.Operator);
        cache.Set("u2", KgsmTier.Operator);

        cache.Remove("u1");

        cache.TryGet("u1", out _).Should().BeFalse();
        cache.TryGet("u2", out _).Should().BeTrue();
    }

    [Fact]
    public void ANoneVerdictIsCachedLikeAnyOther()
    {
        // A denial is an answer. Caching it is what stops a non-member re-hitting Discord's
        // rate-limited member endpoint on every single request they make.
        var cache = Create();
        cache.Set("outsider", KgsmTier.None);

        cache.TryGet("outsider", out var tier).Should().BeTrue();
        tier.Should().Be(KgsmTier.None);
    }
}
