using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

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
        cache.Set("u1", true);

        cache.TryGet("u1", out var hasRole).Should().BeTrue();
        hasRole.Should().BeTrue();
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
        cache.Set("u1", true);
        cache.Remove("u1");

        cache.TryGet("u1", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredEntry_Misses()
    {
        var cache = Create(ttl: 1);
        cache.Set("u1", true);

        await Task.Delay(1300);

        cache.TryGet("u1", out _).Should().BeFalse();
    }
}
