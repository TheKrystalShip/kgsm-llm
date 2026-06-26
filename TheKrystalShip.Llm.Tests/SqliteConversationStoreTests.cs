using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The durable <see cref="SqliteConversationStore"/> over a throwaway temp DB file: the window/isolation/
/// replace semantics (same contract the in-memory store had), plus the two properties that justify the
/// SQLite backing — history SURVIVES a new store instance (a "restart"), and tool-call/tool-result
/// messages round-trip faithfully through the JSON row.
/// </summary>
public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-conv-store-{Guid.NewGuid():N}.db");

    private SqliteConversationStore Create(int maxMessages = 4) =>
        new(Options.Create(new ConversationOptions { MaxMessages = maxMessages, DatabasePath = _dbPath }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void EmptyConversation_ReturnsNoHistory()
    {
        Create().GetHistory("c1").Should().BeEmpty();
    }

    [Fact]
    public void Append_ThenGet_ReturnsMessagesOldestFirst()
    {
        var store = Create();
        store.Append("c1", LlmMessage.User("one"));
        store.Append("c1", LlmMessage.Assistant("two"));

        var history = store.GetHistory("c1");

        history.Select(m => m.Content).Should().ContainInOrder("one", "two");
    }

    [Fact]
    public void Window_TrimsOldestBeyondMaxMessages()
    {
        var store = Create(maxMessages: 3);
        for (var i = 0; i < 5; i++)
            store.Append("c1", LlmMessage.User($"m{i}"));

        var history = store.GetHistory("c1");

        history.Should().HaveCount(3);
        history.Select(m => m.Content).Should().ContainInOrder("m2", "m3", "m4");
    }

    [Fact]
    public void Conversations_AreIsolatedByKey()
    {
        var store = Create();
        store.Append("c1", LlmMessage.User("for c1"));
        store.Append("c2", LlmMessage.User("for c2"));

        store.GetHistory("c1").Should().ContainSingle(m => m.Content == "for c1");
        store.GetHistory("c2").Should().ContainSingle(m => m.Content == "for c2");
    }

    [Fact]
    public void Replace_SwapsEntireHistory()
    {
        var store = Create();
        store.Append("c1", LlmMessage.User("one"), LlmMessage.Assistant("two"));

        store.Replace("c1", LlmMessage.Assistant("summary"));

        store.GetHistory("c1").Select(m => m.Content).Should().Equal("summary");
    }

    [Fact]
    public void Replace_OnUnknownConversation_SeedsIt()
    {
        var store = Create();

        store.Replace("fresh", LlmMessage.Assistant("seed"));

        store.GetHistory("fresh").Should().ContainSingle(m => m.Content == "seed");
    }

    [Fact]
    public void History_SurvivesANewStoreInstance()
    {
        // The whole point of the SQLite backing: a restart (a fresh store over the same file) keeps the
        // conversation. The in-memory store lost this.
        Create().Append("c1", LlmMessage.User("before restart"), LlmMessage.Assistant("ok"));

        var afterRestart = Create();

        afterRestart.GetHistory("c1").Select(m => m.Content)
            .Should().ContainInOrder("before restart", "ok");
    }

    [Fact]
    public void ToolCallAndToolResult_RoundTripFaithfully()
    {
        var store = Create();
        var toolCall = LlmMessage.AssistantToolCalls(new[]
        {
            new LlmToolCall(new Tool("start_server"),
                new Dictionary<string, string?> { ["instance"] = "factorio-1", ["wait"] = null }),
        });
        var toolResult = LlmMessage.Tool(new Tool("start_server"), "started factorio-1");

        store.Append("c1", toolCall, toolResult);

        var history = store.GetHistory("c1");
        history.Should().HaveCount(2);

        history[0].Role.Should().Be(LlmRole.Assistant);
        history[0].ToolCalls.Should().ContainSingle();
        history[0].ToolCalls![0].Name.Name.Should().Be("start_server");
        history[0].ToolCalls![0].Arg("instance").Should().Be("factorio-1");
        history[0].ToolCalls![0].Arguments.Should().ContainKey("wait");
        history[0].ToolCalls![0].Arg("wait").Should().BeNull();

        history[1].Role.Should().Be(LlmRole.Tool);
        history[1].ToolName!.Name.Should().Be("start_server");
        history[1].Content.Should().Be("started factorio-1");
    }
}
