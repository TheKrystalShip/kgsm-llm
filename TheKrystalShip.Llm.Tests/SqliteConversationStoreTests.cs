using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The canonical <see cref="SqliteConversationStore"/> over a throwaway temp DB: the append-only history
/// (turns + checkpoints), the model-context projection (replay from the latest checkpoint forward),
/// non-destructive compaction, isolation by id, durability across a new instance ("restart"), and a
/// faithful round-trip of a turn's tools (incl. the structured card) + thinking.
/// </summary>
public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-conv-store-{Guid.NewGuid():N}.db");

    private SqliteConversationStore Create() =>
        new(Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    private static ConversationTurnRecord Turn(
        string convId, string prompt, string? final,
        IReadOnlyList<RecordedToolCall>? tools = null, string? thinking = null, bool think = false) =>
        new()
        {
            ConversationId = convId,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            UserPrompt = prompt,
            SystemPromptHash = "h",
            Tools = tools ?? Array.Empty<RecordedToolCall>(),
            Iterations = 1,
            Outcome = final is null ? TurnOutcome.Error : TurnOutcome.Ok,
            Think = think,
            Thinking = thinking,
            Final = final,
        };

    [Fact]
    public void Empty_ReturnsNothing()
    {
        var store = Create();
        store.GetHistory("nope").Should().BeEmpty();
        store.GetModelContext("nope").Should().BeEmpty();
    }

    [Fact]
    public void AppendTurn_ProjectsUserThenFinal()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "is terraria up?", "Yes, it's running."));

        store.GetHistory("c1").Should().ContainSingle().Which.Kind.Should().Be(ConversationEntryKind.Turn);
        store.GetModelContext("c1").Select(m => (m.Role, m.Content)).Should().Equal(
            (LlmRole.User, "is terraria up?"), (LlmRole.Assistant, "Yes, it's running."));
    }

    [Fact]
    public void GetModelContext_WithoutCheckpoint_ReplaysEveryTurn_OmittingMissingFinals()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "q1", "a1"));
        store.AppendTurn(Turn("c1", "q2", null));   // a failed turn: user prompt only, no final
        store.AppendTurn(Turn("c1", "q3", "a3"));

        store.GetModelContext("c1").Select(m => (m.Role, m.Content)).Should().Equal(
            (LlmRole.User, "q1"), (LlmRole.Assistant, "a1"),
            (LlmRole.User, "q2"),
            (LlmRole.User, "q3"), (LlmRole.Assistant, "a3"));
    }

    [Fact]
    public void Checkpoint_IsNonDestructive_AndModelContextReplaysFromIt()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "q1", "a1"));
        store.AppendTurn(Turn("c1", "q2", "a2"));
        store.AddCheckpoint("c1", "summary of q1/q2");
        store.AppendTurn(Turn("c1", "q3", "a3"));

        // Full history keeps EVERYTHING — 2 turns + checkpoint + 1 turn.
        store.GetHistory("c1").Select(e => e.Kind).Should().Equal(
            ConversationEntryKind.Turn, ConversationEntryKind.Turn,
            ConversationEntryKind.Checkpoint, ConversationEntryKind.Turn);

        // Model context replays from the checkpoint forward: [summary], then q3.
        var ctx = store.GetModelContext("c1");
        ctx.Should().HaveCount(3);
        ctx[0].Role.Should().Be(LlmRole.Assistant);
        ctx[0].Content.Should().Contain("summary of q1/q2");
        ctx[1].Should().Match<LlmMessage>(m => m.Role == LlmRole.User && m.Content == "q3");
        ctx[2].Should().Match<LlmMessage>(m => m.Role == LlmRole.Assistant && m.Content == "a3");
    }

    [Fact]
    public void Conversations_AreIsolatedById()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "for c1", "r1"));
        store.AppendTurn(Turn("c2", "for c2", "r2"));

        store.GetHistory("c1").Should().ContainSingle();
        store.GetModelContext("c1").Should().Contain(m => m.Content == "for c1");
        store.GetModelContext("c2").Should().Contain(m => m.Content == "for c2");
        store.GetModelContext("c1").Should().NotContain(m => m.Content == "for c2");
    }

    [Fact]
    public void History_SurvivesANewStoreInstance()
    {
        // The point of the SQLite backing: a restart (fresh store, same file) keeps the conversation.
        Create().AppendTurn(Turn("c1", "before restart", "ok"));

        Create().GetModelContext("c1").Select(m => m.Content)
            .Should().ContainInOrder("before restart", "ok");
    }

    [Fact]
    public void Turn_WithToolsThinkingAndCard_RoundTripsThroughHistory()
    {
        var store = Create();
        var card = new Dictionary<string, object?> { ["overall"] = "warn", ["passed"] = 1 };
        var tools = new[]
        {
            new RecordedToolCall(
                new Tool("run_health_check"),
                new Dictionary<string, string?> { ["instance"] = "factorio" },
                "passed with warnings", 42, card),
        };
        store.AppendTurn(Turn("c1", "check it", "all good", tools: tools, thinking: "let me check the health", think: true));

        // Read back through a NEW instance to prove durability AND faithful (de)serialisation.
        var turn = Create().GetHistory("c1").Should().ContainSingle().Subject.Turn!;
        turn.UserPrompt.Should().Be("check it");
        turn.Final.Should().Be("all good");
        turn.Think.Should().BeTrue();
        turn.Thinking.Should().Be("let me check the health");
        turn.Outcome.Should().Be(TurnOutcome.Ok);

        var tool = turn.Tools.Should().ContainSingle().Subject;
        tool.Name.Name.Should().Be("run_health_check");
        tool.Arguments.Should().Contain("instance", "factorio");
        tool.Summary.Should().Be("passed with warnings");
        tool.DurationMs.Should().Be(42);
        tool.Card.Should().NotBeNull();   // the §5·a structured card survived the round-trip (as JSON)
    }
}
