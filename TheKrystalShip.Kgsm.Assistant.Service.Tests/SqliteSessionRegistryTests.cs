using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The registry is what makes a signed token revocable, so these cover the two things a JWT cannot do
/// on its own: die before it expires, and refuse a token that has already been rotated away.
/// </summary>
public class SqliteSessionRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kgsm-session-registry-" + Guid.NewGuid().ToString("N"));

    private SqliteSessionRegistry Registry()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteSessionRegistry(Options.Create(new ConversationOptions
        {
            DatabasePath = Path.Combine(_dir, "conversations.db"),
        }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static SessionRegistration Session(
        string id = "sid_1", string user = "u1", string? jti = "jti_1", DateTimeOffset? expires = null) =>
        new(id, user, "test-host",
            Created: DateTimeOffset.UtcNow,
            Expires: expires ?? DateTimeOffset.UtcNow.AddDays(30),
            UserAgent: "a-browser",
            CurrentJti: jti);

    [Fact]
    public async Task AFreshSessionIsAlive()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session());

        (await registry.IsAliveAsync("sid_1")).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnknownSessionIsNotAlive()
    {
        // The default answer for a sid nobody recorded is "no". A token whose session cannot be found
        // authenticates nobody, rather than being waved through because there is nothing to check it against.
        SqliteSessionRegistry registry = Registry();

        (await registry.IsAliveAsync("sid_never_created")).Should().BeFalse();
    }

    [Fact]
    public async Task ARevokedSessionIsNotAlive()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session());

        (await registry.RevokeAsync("sid_1")).Should().BeTrue();
        (await registry.IsAliveAsync("sid_1")).Should().BeFalse();
    }

    [Fact]
    public async Task RevokingTwiceReportsNothingLeftToKill()
    {
        // So a double logout is not counted as two revocations.
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session());

        (await registry.RevokeAsync("sid_1")).Should().BeTrue();
        (await registry.RevokeAsync("sid_1")).Should().BeFalse();
    }

    [Fact]
    public async Task APastItsCapSessionIsNotAlive()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session(expires: DateTimeOffset.UtcNow.AddSeconds(-1)));

        (await registry.IsAliveAsync("sid_1")).Should().BeFalse();
    }

    [Fact]
    public async Task RotationRequiresThePresentedJtiToBeTheCurrentOne()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session(jti: "jti_1"));

        (await registry.RotateAsync("sid_1", "jti_1", "jti_2", DateTimeOffset.UtcNow.AddDays(30)))
            .Should().BeTrue();

        // The token that was just rotated away. Refusing this is the reuse detection.
        (await registry.RotateAsync("sid_1", "jti_1", "jti_3", DateTimeOffset.UtcNow.AddDays(30)))
            .Should().BeFalse();

        (await registry.RotateAsync("sid_1", "jti_2", "jti_3", DateTimeOffset.UtcNow.AddDays(30)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ARevokedSessionCannotBeRotatedBackToLife()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session(jti: "jti_1"));
        await registry.RevokeAsync("sid_1");

        (await registry.RotateAsync("sid_1", "jti_1", "jti_2", DateTimeOffset.UtcNow.AddDays(30)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task RotationSlidesTheCapForward()
    {
        // How someone who keeps using the panel stays signed in: the window moves rather than counting
        // down from the original login.
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session(jti: "jti_1", expires: DateTimeOffset.UtcNow.AddSeconds(30)));

        await registry.RotateAsync("sid_1", "jti_1", "jti_2", DateTimeOffset.UtcNow.AddDays(30));

        await registry.DeleteExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        (await registry.IsAliveAsync("sid_1")).Should().BeTrue();
    }

    [Fact]
    public async Task TheSweepRemovesOnlyWhatIsPastItsCap()
    {
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session("sid_live"));
        await registry.CreateAsync(Session("sid_dead", expires: DateTimeOffset.UtcNow.AddSeconds(-1)));

        (await registry.DeleteExpiredAsync(DateTimeOffset.UtcNow)).Should().Be(1);
        (await registry.IsAliveAsync("sid_live")).Should().BeTrue();
    }

    [Fact]
    public async Task ARevokedButUnexpiredSessionSurvivesTheSweepAsATombstone()
    {
        // Deleting it early would let a replayed refresh land on "unknown" instead of "revoked". Both
        // refuse, but only the tombstone is honest about why.
        SqliteSessionRegistry registry = Registry();
        await registry.CreateAsync(Session());
        await registry.RevokeAsync("sid_1");

        (await registry.DeleteExpiredAsync(DateTimeOffset.UtcNow)).Should().Be(0);
    }

    [Fact]
    public async Task SessionsSurviveTheProcessThatMintedThem()
    {
        // The whole reason for SQLite over a dictionary: a restart must not sign everyone out, and a
        // revocation must outlive the process that performed it.
        SqliteSessionRegistry first = Registry();
        await first.CreateAsync(Session("sid_kept"));
        await first.CreateAsync(Session("sid_killed"));
        await first.RevokeAsync("sid_killed");

        SqliteSessionRegistry reopened = Registry();

        (await reopened.IsAliveAsync("sid_kept")).Should().BeTrue();
        (await reopened.IsAliveAsync("sid_killed")).Should().BeFalse();
    }
}
