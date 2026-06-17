using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

public class InMemoryConversationStoreTests
{
    private static InMemoryConversationStore Create(int maxMessages = 4) =>
        new(Options.Create(new ConversationOptions { MaxMessages = maxMessages, IdleTimeoutMinutes = 15 }));

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
}
