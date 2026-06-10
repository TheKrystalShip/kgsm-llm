using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class SessionStoreTests
{
    private static Session ValidSession(string userId = "u1") =>
        new(userId, "Display", "discord-access-token", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void Create_Then_TryGet_RoundTrips()
    {
        var store = new SessionStore();
        var token = store.Create(ValidSession("alice"));

        store.TryGet(token, out var session).Should().BeTrue();
        session.DiscordUserId.Should().Be("alice");
        session.AccessToken.Should().Be("discord-access-token");
    }

    [Fact]
    public void Token_Is256BitCsprng_AndUnique()
    {
        var store = new SessionStore();
        var a = store.Create(ValidSession());
        var b = store.Create(ValidSession());

        a.Should().NotBe(b);
        Base64Url.Decode(a).Length.Should().Be(32); // 256-bit
    }

    [Fact]
    public void ExpiredSession_IsNotReturned_AndEvicted()
    {
        var store = new SessionStore();
        var token = store.Create(new Session("u1", "D", "t", DateTimeOffset.UtcNow.AddSeconds(-1)));

        store.TryGet(token, out _).Should().BeFalse();
        // Evicted: even a fresh lookup stays false.
        store.TryGet(token, out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_LogsOut()
    {
        var store = new SessionStore();
        var token = store.Create(ValidSession());

        store.Remove(token);

        store.TryGet(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGet_UnknownOrNull_ReturnsFalse()
    {
        var store = new SessionStore();
        store.TryGet("nope", out _).Should().BeFalse();
        store.TryGet(null, out _).Should().BeFalse();
        store.TryGet("", out _).Should().BeFalse();
    }
}
