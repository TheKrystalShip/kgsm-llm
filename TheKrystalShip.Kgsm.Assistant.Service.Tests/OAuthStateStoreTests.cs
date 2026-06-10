using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class OAuthStateStoreTests
{
    private static OAuthStateStore Create(int ttl = 300) =>
        new(Options.Create(new AuthOptions { StateTtlSeconds = ttl }));

    [Fact]
    public void Consume_ReturnsVerifier_Once()
    {
        var store = Create();
        var state = store.Create("the-verifier");

        store.TryConsume(state, out var verifier).Should().BeTrue();
        verifier.Should().Be("the-verifier");

        // Single-use: a replay of the same state is rejected.
        store.TryConsume(state, out _).Should().BeFalse();
    }

    [Fact]
    public void UnknownOrNullState_ReturnsFalse()
    {
        var store = Create();
        store.TryConsume("never-issued", out _).Should().BeFalse();
        store.TryConsume(null, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredState_IsRejected()
    {
        var store = Create(ttl: 1);
        var state = store.Create("v");

        await Task.Delay(1300);

        store.TryConsume(state, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Create_SweepsExpiredStates_BoundingTheStore()
    {
        var store = Create(ttl: 1);
        store.Create("v1");
        store.Create("v2");
        store.Create("v3");

        await Task.Delay(1300);

        store.Create("fresh"); // sweep on create drops the three abandoned states
        store.Count.Should().Be(1);
    }
}
