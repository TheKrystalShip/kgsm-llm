using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// <see cref="MemoryScope"/>: the owner a conversation's memories belong to. The last test is the
/// important one — the same rule is computed in SQL by <see cref="SqliteConversationStore"/> to derive
/// an actor, and nothing but that test would notice the two drifting apart.
/// </summary>
public sealed class MemoryScopeTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-memscope-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    [Theory]
    // A per-chat id resolves to its owner — this is what makes memory cross a chat boundary.
    [InlineData("web:alice:chat7", "web:alice")]
    [InlineData("web:alice:chat7:extra", "web:alice")]
    // A bare per-user id is already an owner.
    [InlineData("web:alice", "web:alice")]
    // A room is keyed to a place, has no user segment, and owns its memories itself.
    [InlineData("room:g1-v42", "room:g1-v42")]
    // An id with no separator at all is its own owner rather than an error.
    [InlineData("cli", "cli")]
    public void OwnerOf_TakesEverythingUpToTheSecondSeparator(string conversationId, string expected) =>
        MemoryScope.OwnerOf(conversationId).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OwnerOf_BlankIsEmpty_SoTheTurnHasNoOwner(string? conversationId) =>
        MemoryScope.OwnerOf(conversationId).Should().BeEmpty();

    [Fact]
    public void OwnerOf_DoesNotConfuseUsersWhoseIdsSharePrefix()
    {
        MemoryScope.OwnerOf("web:alice:c1").Should().Be("web:alice");
        MemoryScope.OwnerOf("web:alice2:c1").Should().Be("web:alice2");
        MemoryScope.OwnerOf("web:alice:c1").Should().NotBe(MemoryScope.OwnerOf("web:alice2:c1"));
    }

    /// <summary>
    /// Pins the C# rule to the SQL one. The store groups conversations into actors with its own
    /// <c>ActorSql</c>; a query cannot call <see cref="MemoryScope.OwnerOf"/>, so the rule lives in two
    /// languages and this is the only thing that says they still agree. If this fails, one of them
    /// moved and the other has to follow — do not "fix" it by changing the expectation.
    /// </summary>
    [Fact]
    public void OwnerOf_AgreesWithTheStoresActorGrouping()
    {
        var store = new SqliteConversationStore(
            Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

        string[] ids =
        [
            "web:alice:chat1",
            "web:alice:chat2",
            "web:alice",
            "web:alice2:chat1",
            "web:bob:chat1",
        ];

        foreach (var id in ids)
            store.AppendTurn(Turn(id));

        // What the store says the actors are, as owner keys.
        var fromSql = store.ListActors("web")
            .Select(a => $"{a.Surface}:{a.UserId}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        // What the C# rule says they are, from the same ids.
        var fromCSharp = ids
            .Select(MemoryScope.OwnerOf)
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        fromCSharp.Should().Equal(fromSql);
    }

    private static ConversationTurnRecord Turn(string convId) =>
        new()
        {
            ConversationId = convId,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            UserPrompt = "hello",
            SystemPromptHash = "h",
            Tools = [],
            Iterations = 1,
            Outcome = TurnOutcome.Ok,
            Think = false,
            Final = "hi",
        };
}
