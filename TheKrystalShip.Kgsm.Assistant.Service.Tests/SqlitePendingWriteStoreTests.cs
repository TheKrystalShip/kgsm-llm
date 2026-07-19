using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// <see cref="SqlitePendingWriteStore"/> over a throwaway temp DB (the same file
/// <see cref="TheKrystalShip.Llm.Conversation.SqliteConversationStore"/> would use — this store just adds
/// its own table): single-use take, TTL expiry, and durability across a fresh instance ("restart").
/// </summary>
public sealed class SqlitePendingWriteStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-pending-writes-{Guid.NewGuid():N}.db");

    private SqlitePendingWriteStore Create() =>
        new(Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Put_ThenTryTake_ReturnsTheSameContent()
    {
        var store = Create();
        var id = store.Put("hello world", DateTimeOffset.UtcNow.AddMinutes(5));

        store.TryTake(id, out var content).Should().BeTrue();
        content.Should().Be("hello world");
    }

    [Fact]
    public void TryTake_IsSingleUse_SecondTakeFails()
    {
        var store = Create();
        var id = store.Put("only once", DateTimeOffset.UtcNow.AddMinutes(5));

        store.TryTake(id, out _).Should().BeTrue();
        store.TryTake(id, out var second).Should().BeFalse();
        second.Should().BeEmpty();
    }

    [Fact]
    public void TryTake_UnknownId_Fails()
    {
        var store = Create();

        store.TryTake(Guid.NewGuid().ToString("N"), out var content).Should().BeFalse();
        content.Should().BeEmpty();
    }

    [Fact]
    public void TryTake_ExpiredEntry_Fails_AndIsConsumed()
    {
        var store = Create();
        var id = store.Put("stale", DateTimeOffset.UtcNow.AddSeconds(-1)); // already expired

        store.TryTake(id, out var content).Should().BeFalse();
        content.Should().BeEmpty();

        // Consumed on the expired peek too — a second take must not somehow succeed.
        store.TryTake(id, out _).Should().BeFalse();
    }

    [Fact]
    public void Put_SweepsExpiredRowsOpportunistically()
    {
        var store = Create();
        var staleId = store.Put("stale", DateTimeOffset.UtcNow.AddSeconds(-60));

        // A later Put sweeps rows already past their TTL.
        store.Put("fresh", DateTimeOffset.UtcNow.AddMinutes(5));

        store.TryTake(staleId, out _).Should().BeFalse();
    }

    [Fact]
    public void SurvivesAFreshInstance_SameDatabaseFile()
    {
        var id = Create().Put("durable across restart", DateTimeOffset.UtcNow.AddMinutes(5));

        // A brand-new store instance over the SAME file — mirrors a Service restart within the TTL.
        var reopened = Create();
        reopened.TryTake(id, out var content).Should().BeTrue();
        content.Should().Be("durable across restart");
    }

    [Fact]
    public void DistinctPuts_YieldDistinctIds()
    {
        var store = Create();
        var id1 = store.Put("a", DateTimeOffset.UtcNow.AddMinutes(5));
        var id2 = store.Put("b", DateTimeOffset.UtcNow.AddMinutes(5));

        id1.Should().NotBe(id2);
    }
}
