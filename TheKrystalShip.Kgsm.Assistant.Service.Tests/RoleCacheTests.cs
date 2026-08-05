using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class RoleCacheTests
{
    private const string ActionRole = "role-action";
    private const string ReviewRole = "role-review";

    private static RoleCache Create(int ttl = 60) =>
        new(Options.Create(new AuthOptions { RoleCacheTtlSeconds = ttl }));

    [Fact]
    public void Set_Then_TryGet_HitsWithinTtl()
    {
        var cache = Create();
        cache.Set(ActionRole, "u1", true);

        cache.TryGet(ActionRole, "u1", out var hasRole).Should().BeTrue();
        hasRole.Should().BeTrue();
    }

    [Fact]
    public void TryGet_Unknown_Misses()
    {
        Create().TryGet(ActionRole, "nobody", out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_Evicts()
    {
        var cache = Create();
        cache.Set(ActionRole, "u1", true);
        cache.Remove("u1");

        cache.TryGet(ActionRole, "u1", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredEntry_Misses()
    {
        var cache = Create(ttl: 1);
        cache.Set(ActionRole, "u1", true);

        await Task.Delay(1300);

        cache.TryGet(ActionRole, "u1", out _).Should().BeFalse();
    }

    [Fact]
    public void OneRolesAnswer_IsNeverServedForAnother()
    {
        // The service asks about more than one role for the same user. A per-user-only slot would let
        // "may act" answer "may review" — the reason the key carries the role.
        var cache = Create();
        cache.Set(ActionRole, "u1", true);

        cache.TryGet(ReviewRole, "u1", out _).Should().BeFalse();

        cache.Set(ReviewRole, "u1", false);
        cache.TryGet(ActionRole, "u1", out var canAct).Should().BeTrue();
        canAct.Should().BeTrue();
        cache.TryGet(ReviewRole, "u1", out var canReview).Should().BeTrue();
        canReview.Should().BeFalse();
    }

    [Fact]
    public void Remove_EvictsEveryRoleForThatUser()
    {
        // Logout drops the user entirely — leaving one role's decision behind would outlive the session.
        var cache = Create();
        cache.Set(ActionRole, "u1", true);
        cache.Set(ReviewRole, "u1", true);
        cache.Set(ActionRole, "u2", true);

        cache.Remove("u1");

        cache.TryGet(ActionRole, "u1", out _).Should().BeFalse();
        cache.TryGet(ReviewRole, "u1", out _).Should().BeFalse();
        cache.TryGet(ActionRole, "u2", out _).Should().BeTrue();   // another user is untouched
    }
}
