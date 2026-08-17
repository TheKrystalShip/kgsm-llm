using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The three memory tools as the model meets them: what they store, and — mostly — how they refuse.
/// Every refusal here has to name what to do instead, because a refusal that does not gets the
/// identical call re-sent.
/// </summary>
public sealed class MemoryToolTests
{
    private readonly InMemoryMemoryStore _memories = new(maxPerOwner: 3);

    private ToolDispatcher Create() =>
        new(Substitute.For<IServerOperations>(), Substitute.For<IServerInventory>(),
            Substitute.For<IConfirmationContext>(), Substitute.For<ISearch>(),
            Substitute.For<IWebFetch>(), Substitute.For<IServerMetrics>(),
            Substitute.For<IEventHistory>(), Substitute.For<INetworkInfo>(),
            Substitute.For<IUpnpInfo>(), Substitute.For<IServerFacts>(),
            Substitute.For<IHostFacts>(), Substitute.For<IBlueprintAuthoring>(),
            ShippedText.Catalog,
            new SettlementTiming(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10)),
            _memories, Options.Create(new MemoryOptions { MaxPerOwner = 3, MaxSummaryLength = 200 }),
            NullLogger<ToolDispatcher>.Instance);

    private async Task<string> Run(Capability capability, Dictionary<string, string?> args) =>
        (await Create().ExecuteAsync(new LlmToolCall(ShippedText.Name(capability), args))).Summary;

    private static Dictionary<string, string?> Args(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // ── writing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remember_StoresItAgainstTheTurnsOwner()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");

        var reply = await Run(LlmTools.Remember,
            Args(("key", "Preferred test game"), ("summary", "Tests with Factorio."), ("body", "Detail.")));

        reply.Should().Contain("preferred-test-game");
        var stored = _memories.Get("web:alice", "preferred-test-game");
        stored.Should().NotBeNull();
        stored!.Summary.Should().Be("Tests with Factorio.");
        stored.Body.Should().Be("Detail.");
    }

    [Fact]
    public async Task Remember_SameKeyAgain_Corrects()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");

        await Run(LlmTools.Remember, Args(("key", "game"), ("summary", "Factorio.")));
        await Run(LlmTools.Remember, Args(("key", "game"), ("summary", "Terraria now.")));

        _memories.List("web:alice").Should().ContainSingle()
            .Which.Summary.Should().Be("Terraria now.");
    }

    [Fact]
    public async Task Remember_WithNoOwner_RefusesAndOffersNothing()
    {
        // No MemoryOwner scope: a turn whose conversation resolved to no owner.
        var reply = await Run(LlmTools.Remember, Args(("key", "game"), ("summary", "Factorio.")));

        reply.Should().StartWith("Error:");
        reply.Should().Contain("no memory");
        // It must tell the model not to promise remembering, or the reply offers what cannot happen.
        reply.Should().Contain("do not offer to remember");
        _memories.Count("web:alice").Should().Be(0);
    }

    [Fact]
    public async Task Remember_WithoutAKey_SaysWhatAKeyIs()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        var reply = await Run(LlmTools.Remember, Args(("summary", "Tests with Factorio.")));

        reply.Should().StartWith("Error:");
        reply.Should().Contain("'key'");
    }

    [Fact]
    public async Task Remember_WithoutASummary_SaysWhatASummaryIs()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        var reply = await Run(LlmTools.Remember, Args(("key", "game")));

        reply.Should().StartWith("Error:");
        reply.Should().Contain("'summary'");
    }

    [Fact]
    public async Task Remember_OversizedSummary_IsRefusedRatherThanTruncated()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        var reply = await Run(LlmTools.Remember,
            Args(("key", "game"), ("summary", new string('x', 400))));

        // Truncating would store something the model believes it wrote and did not.
        reply.Should().StartWith("Error:");
        reply.Should().Contain("200");
        _memories.Get("web:alice", "game").Should().BeNull();
    }

    [Fact]
    public async Task Remember_AtTheCap_RefusesAndNamesTheWayOut()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        await Run(LlmTools.Remember, Args(("key", "one"), ("summary", "One.")));
        await Run(LlmTools.Remember, Args(("key", "two"), ("summary", "Two.")));
        await Run(LlmTools.Remember, Args(("key", "three"), ("summary", "Three.")));

        var reply = await Run(LlmTools.Remember, Args(("key", "four"), ("summary", "Four.")));

        reply.Should().StartWith("Error:");
        reply.Should().Contain("3");
        // The refusal has to name the action that clears the way, not just the limit.
        reply.Should().Contain(ShippedText.Name(LlmTools.Forget).Name);
        _memories.Get("web:alice", "one").Should().NotBeNull("nothing is evicted to make room");
    }

    // ── forgetting ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Forget_DropsIt()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        await Run(LlmTools.Remember, Args(("key", "game"), ("summary", "Factorio.")));

        var reply = await Run(LlmTools.Forget, Args(("key", "game")));

        reply.Should().Contain("game");
        _memories.Get("web:alice", "game").Should().BeNull();
    }

    [Fact]
    public async Task Forget_UnknownKey_ListsWhatDoesExist()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        await Run(LlmTools.Remember, Args(("key", "preferred-game"), ("summary", "Factorio.")));

        var reply = await Run(LlmTools.Forget, Args(("key", "nonsense")));

        // Naming the keys that exist is what stops the model re-sending the same wrong key.
        reply.Should().Contain("preferred-game");
    }

    // ── recalling ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recall_ReadsTheBody_AndDatesIt()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        await Run(LlmTools.Remember,
            Args(("key", "game"), ("summary", "Tests with Factorio."), ("body", "Because it boots fast.")));

        var reply = await Run(LlmTools.Recall, Args(("key", "game")));

        reply.Should().Contain("Because it boots fast.");
        // Dated, so a memory that has gone stale is at least visibly old.
        reply.Should().Contain("written");
    }

    [Fact]
    public async Task Recall_NothingRemembered_SaysSo()
    {
        using var _ = MemoryOwner.BeginTurn("web:alice");
        var reply = await Run(LlmTools.Recall, Args(("key", "game")));

        reply.Should().Contain("Nothing is remembered");
    }

    [Fact]
    public async Task OneOwnersMemoryIsNeverAnothers()
    {
        using (var _ = MemoryOwner.BeginTurn("web:alice"))
            await Run(LlmTools.Remember, Args(("key", "game"), ("summary", "Alice's.")));

        using (var _ = MemoryOwner.BeginTurn("web:bob"))
        {
            var reply = await Run(LlmTools.Recall, Args(("key", "game")));
            reply.Should().NotContain("Alice's.");
        }
    }

    [Fact]
    public async Task ARoomOwnsItsOwnMemory()
    {
        using (var _ = MemoryOwner.BeginTurn(MemoryScope.OwnerOf("room:g1-v42")))
            await Run(LlmTools.Remember, Args(("key", "rules"), ("summary", "No pinging at night.")));

        _memories.Get("room:g1-v42", "rules").Should().NotBeNull();
        _memories.Count("web:alice").Should().Be(0);
    }
}
