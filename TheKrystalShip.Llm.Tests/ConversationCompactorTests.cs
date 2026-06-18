using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Drives <see cref="ConversationCompactor"/> with a fake <see cref="ILlmClient"/> over a real
/// <see cref="InMemoryConversationStore"/>: verifies the no-op thresholds, that a successful
/// compaction REPLACES the history with one model-role summary, and that failures / empty
/// summaries leave the stored history untouched.
/// </summary>
public class ConversationCompactorTests
{
    private const string Conversation = "cli:abc";

    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly InMemoryConversationStore _store =
        new(Options.Create(new ConversationOptions { MaxMessages = 12, IdleTimeoutMinutes = 60 }));

    private IReadOnlyList<LlmMessage>? _sent;
    private IReadOnlyList<LlmToolDefinition>? _tools;
    private int _calls;

    private ConversationCompactor Create() =>
        new(_llm, _store, NullLogger<ConversationCompactor>.Instance);

    private void ScriptModel(Result<LlmResponse> response) =>
        _llm.ChatAsync(
                Arg.Do<IReadOnlyList<LlmMessage>>(m => { _sent = m.ToList(); _calls++; }),
                Arg.Do<IReadOnlyList<LlmToolDefinition>?>(t => _tools = t),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

    private void Seed(params LlmMessage[] messages) => _store.Append(Conversation, messages);

    [Fact]
    public async Task EmptyConversation_IsNoOp_AndNeverCallsModel()
    {
        ScriptModel(Result.Success(LlmResponse.Text("unused")));

        var result = await Create().CompactAsync(Conversation);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Compacted.Should().BeFalse();
        _calls.Should().Be(0);
    }

    [Fact]
    public async Task SingleMessage_IsNoOp()
    {
        Seed(LlmMessage.User("is terraria up?"));
        ScriptModel(Result.Success(LlmResponse.Text("unused")));

        var result = await Create().CompactAsync(Conversation);

        result.Value!.Compacted.Should().BeFalse();
        _calls.Should().Be(0);
        _store.GetHistory(Conversation).Should().HaveCount(1);   // untouched
    }

    [Fact]
    public async Task MultiMessage_Compacts_ReplacingHistoryWithOneModelSummary()
    {
        Seed(
            LlmMessage.User("is terraria up?"),
            LlmMessage.Assistant("Yes, terraria-1 is running."),
            LlmMessage.User("how much RAM is it using?"),
            LlmMessage.Assistant("About 1.2 GB."));
        ScriptModel(Result.Success(LlmResponse.Text("User asked about terraria-1: it is up, ~1.2 GB RAM.")));

        var result = await Create().CompactAsync(Conversation);

        // Outcome reports what was folded away.
        result.IsSuccess.Should().BeTrue();
        result.Value!.Compacted.Should().BeTrue();
        result.Value!.MessagesCompacted.Should().Be(4);

        // History is now exactly one assistant-role recap carrying the summary.
        var history = _store.GetHistory(Conversation);
        history.Should().ContainSingle();
        history[0].Role.Should().Be(LlmRole.Assistant);
        history[0].Content.Should().Contain("User asked about terraria-1");

        // The summarization call was tool-less and saw a system instruction + the transcript.
        _tools.Should().BeNull();
        _sent.Should().HaveCount(2);
        _sent![0].Role.Should().Be(LlmRole.System);
        _sent![1].Role.Should().Be(LlmRole.User);
        _sent![1].Content.Should().Contain("User: is terraria up?")
            .And.Contain("Assistant: Yes, terraria-1 is running.");
    }

    [Fact]
    public async Task ModelFailure_ReturnsFailure_AndLeavesHistoryUntouched()
    {
        Seed(LlmMessage.User("a"), LlmMessage.Assistant("b"));
        ScriptModel(Result.Failure<LlmResponse>("ollama unreachable"));

        var result = await Create().CompactAsync(Conversation);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("ollama unreachable");
        _store.GetHistory(Conversation).Should().HaveCount(2);   // unchanged
    }

    [Fact]
    public async Task EmptyModelSummary_Fails_AndLeavesHistoryUntouched()
    {
        Seed(LlmMessage.User("a"), LlmMessage.Assistant("b"));
        ScriptModel(Result.Success(LlmResponse.Text("   ")));

        var result = await Create().CompactAsync(Conversation);

        result.IsFailure.Should().BeTrue();
        _store.GetHistory(Conversation).Should().HaveCount(2);   // unchanged
    }
}
