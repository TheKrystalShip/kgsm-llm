using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// <see cref="SqliteMemoryStore"/> over a throwaway temp DB: append-only writes resolved latest-wins,
/// forgetting as a tombstone that keeps the log, isolation between owners, the per-owner cap, and
/// durability across a new instance ("restart").
/// </summary>
public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-mem-store-{Guid.NewGuid():N}.db");

    private SqliteMemoryStore Create(int maxPerOwner = 64) =>
        new(Options.Create(new ConversationOptions { DatabasePath = _dbPath }),
            Options.Create(new MemoryOptions { MaxPerOwner = maxPerOwner }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    private static MemoryRecord Memory(string key, string summary = "a summary", string body = "a body",
        string? origin = "web:alice:chat1") =>
        new(key, summary, body, DateTimeOffset.UtcNow, origin);

    [Fact]
    public void UnknownOwner_RemembersNothing()
    {
        var store = Create();
        store.List("web:nobody").Should().BeEmpty();
        store.Get("web:nobody", "anything").Should().BeNull();
        store.Count("web:nobody").Should().Be(0);
    }

    [Fact]
    public void Write_RoundTrips()
    {
        var store = Create();
        store.Write("web:alice", Memory("factorio-for-tests", "Tests with Factorio.", "The long version."))
            .Should().BeTrue();

        var got = store.Get("web:alice", "factorio-for-tests");
        got.Should().NotBeNull();
        got!.Summary.Should().Be("Tests with Factorio.");
        got.Body.Should().Be("The long version.");
        got.Origin.Should().Be("web:alice:chat1");
        store.List("web:alice").Should().ContainSingle();
    }

    [Fact]
    public void RewritingAKey_SupersedesRatherThanDuplicating()
    {
        var store = Create();
        store.Write("web:alice", Memory("game", "Prefers Factorio."));
        store.Write("web:alice", Memory("game", "Prefers Terraria now."));

        store.List("web:alice").Should().ContainSingle()
            .Which.Summary.Should().Be("Prefers Terraria now.");
        store.Count("web:alice").Should().Be(1);
    }

    [Fact]
    public void Forget_HidesIt_ButTheWriteStaysInTheLog()
    {
        var store = Create();
        store.Write("web:alice", Memory("game"));

        store.Forget("web:alice", "game").Should().BeTrue();
        store.Get("web:alice", "game").Should().BeNull();
        store.List("web:alice").Should().BeEmpty();
        store.Count("web:alice").Should().Be(0);

        // Append-only: the tombstone hid the memory, it did not remove the row that carried it. A row
        // count over the raw table is the only way to see that from outside the resolved view.
        RawRowCount("web:alice", "game").Should().Be(2);
    }

    [Fact]
    public void Forget_UnknownKey_ReportsIt()
    {
        var store = Create();
        store.Forget("web:alice", "never-written").Should().BeFalse();
    }

    [Fact]
    public void WritingAForgottenKeyAgain_RemembersIt()
    {
        var store = Create();
        store.Write("web:alice", Memory("game", "First."));
        store.Forget("web:alice", "game");
        store.Write("web:alice", Memory("game", "Second.")).Should().BeTrue();

        store.Get("web:alice", "game")!.Summary.Should().Be("Second.");
        store.Count("web:alice").Should().Be(1);
    }

    [Fact]
    public void OwnersAreIsolated()
    {
        var store = Create();
        store.Write("web:alice", Memory("game", "Alice's."));
        store.Write("web:bob", Memory("game", "Bob's."));

        store.Get("web:alice", "game")!.Summary.Should().Be("Alice's.");
        store.Get("web:bob", "game")!.Summary.Should().Be("Bob's.");
        store.List("web:alice").Should().ContainSingle();
    }

    [Fact]
    public void OwnersWhoseKeysSharePrefix_StayApart()
    {
        // The owner key is matched exactly, never by prefix — so web:alice cannot read web:alice2.
        var store = Create();
        store.Write("web:alice", Memory("game", "Alice's."));
        store.Write("web:alice2", Memory("game", "Alice2's."));

        store.List("web:alice").Should().ContainSingle()
            .Which.Summary.Should().Be("Alice's.");
    }

    [Fact]
    public void RoomsOwnTheirMemories()
    {
        var store = Create();
        store.Write("room:g1-v42", Memory("channel-rules"));

        store.List("room:g1-v42").Should().ContainSingle();
        store.List("web:alice").Should().BeEmpty();
    }

    [Fact]
    public void Cap_RefusesANewKey_ButStillAllowsARewrite()
    {
        var store = Create(maxPerOwner: 3);
        store.Write("web:alice", Memory("one")).Should().BeTrue();
        store.Write("web:alice", Memory("two")).Should().BeTrue();
        store.Write("web:alice", Memory("three")).Should().BeTrue();

        // A fourth distinct key is refused rather than evicting the oldest.
        store.Write("web:alice", Memory("four")).Should().BeFalse();
        store.Get("web:alice", "one").Should().NotBeNull();
        store.Count("web:alice").Should().Be(3);

        // Correcting an existing memory adds nothing to the count, so it is allowed at the cap —
        // otherwise a full owner could never fix a memory that is wrong.
        store.Write("web:alice", Memory("two", "Corrected.")).Should().BeTrue();
        store.Get("web:alice", "two")!.Summary.Should().Be("Corrected.");
    }

    [Fact]
    public void Cap_ForgettingMakesRoomAgain()
    {
        var store = Create(maxPerOwner: 2);
        store.Write("web:alice", Memory("one"));
        store.Write("web:alice", Memory("two"));
        store.Write("web:alice", Memory("three")).Should().BeFalse();

        store.Forget("web:alice", "one");
        store.Write("web:alice", Memory("three")).Should().BeTrue();
    }

    [Fact]
    public void List_IsNewestWrittenFirst()
    {
        var store = Create();
        var older = new MemoryRecord("older", "Older.", "b", DateTimeOffset.UtcNow.AddDays(-2), null);
        var newer = new MemoryRecord("newer", "Newer.", "b", DateTimeOffset.UtcNow, null);
        store.Write("web:alice", older);
        store.Write("web:alice", newer);

        store.List("web:alice").Select(m => m.Key).Should().Equal("newer", "older");
    }

    [Fact]
    public void SurvivesARestart()
    {
        var first = Create();
        first.Write("web:alice", Memory("game", "Prefers Factorio."));
        first.Forget("web:alice", "game");
        first.Write("web:alice", Memory("other", "Something else."));

        // A second instance over the same file resolves to exactly the same standing memories.
        var second = Create();
        second.List("web:alice").Should().ContainSingle()
            .Which.Key.Should().Be("other");
        second.Get("web:alice", "game").Should().BeNull();
    }

    [Fact]
    public void BlankOwnerOrKey_WritesNothing()
    {
        // A turn that could not resolve an owner must never fall back to a shared namespace.
        var store = Create();
        store.Write("", Memory("game")).Should().BeFalse();
        store.Write("web:alice", Memory("")).Should().BeFalse();
        store.Forget("", "game").Should().BeFalse();
        store.List("").Should().BeEmpty();
        store.Get("", "game").Should().BeNull();
    }

    /// <summary>Rows in the raw append-only table for one key — how a test sees that nothing was
    /// removed, since every read path resolves the log rather than exposing it.</summary>
    private int RawRowCount(string ownerKey, string memoryKey)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM memory_entries WHERE owner_key = $owner AND memory_key = $key;";
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$key", memoryKey);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
