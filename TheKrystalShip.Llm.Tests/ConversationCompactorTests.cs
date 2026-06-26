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
/// <see cref="SqliteConversationStore"/> (a throwaway temp DB): verifies the no-op threshold, that a
/// successful compaction appends a <b>checkpoint</b> NON-DESTRUCTIVELY (prior turns stay in the
/// history; the model context replays from the summary forward), and that failures / empty summaries
/// add no checkpoint.
/// </summary>
public sealed class ConversationCompactorTests : IDisposable
{
    private const string Conversation = "cli:abc";

    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-conv-compactor-{Guid.NewGuid():N}.db");
    private readonly SqliteConversationStore _store;

    public ConversationCompactorTests() =>
        _store = new SqliteConversationStore(
            Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

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

    private void SeedTurn(string prompt, string? final) =>
        _store.AppendTurn(new ConversationTurnRecord
        {
            ConversationId = Conversation,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            UserPrompt = prompt,
            SystemPromptHash = "h",
            Tools = Array.Empty<RecordedToolCall>(),
            Iterations = 1,
            Outcome = final is null ? TurnOutcome.Error : TurnOutcome.Ok,
            Think = false,
            Final = final,
        });

    private int TurnCount() =>
        _store.GetHistory(Conversation).Count(e => e.Kind == ConversationEntryKind.Turn);

    private int CheckpointCount() =>
        _store.GetHistory(Conversation).Count(e => e.Kind == ConversationEntryKind.Checkpoint);

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
    public async Task BelowThreshold_IsNoOp()
    {
        // A single failed turn projects to ONE context message (the user prompt, no final) — below the
        // 2-message floor, so there is nothing worth folding.
        SeedTurn("is terraria up?", final: null);
        ScriptModel(Result.Success(LlmResponse.Text("unused")));

        var result = await Create().CompactAsync(Conversation);

        result.Value!.Compacted.Should().BeFalse();
        _calls.Should().Be(0);
        CheckpointCount().Should().Be(0);
    }

    [Fact]
    public async Task MultiTurn_Compacts_AppendsCheckpoint_NonDestructively()
    {
        SeedTurn("is terraria up?", "Yes, terraria-1 is running.");
        SeedTurn("how much RAM is it using?", "About 1.2 GB.");
        ScriptModel(Result.Success(LlmResponse.Text("User asked about terraria-1: it is up, ~1.2 GB RAM.")));

        var result = await Create().CompactAsync(Conversation);

        // Outcome reports the context size folded into the checkpoint (2 turns → 4 messages).
        result.IsSuccess.Should().BeTrue();
        result.Value!.Compacted.Should().BeTrue();
        result.Value!.MessagesCompacted.Should().Be(4);

        // NON-DESTRUCTIVE: the two original turns are STILL in the history, plus a new checkpoint.
        TurnCount().Should().Be(2);
        CheckpointCount().Should().Be(1);

        // The model now replays from the checkpoint forward — one recap message carrying the summary.
        var ctx = _store.GetModelContext(Conversation);
        ctx.Should().ContainSingle();
        ctx[0].Role.Should().Be(LlmRole.Assistant);
        ctx[0].Content.Should().Contain("User asked about terraria-1");

        // The summarization call was tool-less and saw a system instruction + the rendered transcript.
        _tools.Should().BeNull();
        _sent.Should().HaveCount(2);
        _sent![0].Role.Should().Be(LlmRole.System);
        _sent![1].Role.Should().Be(LlmRole.User);
        _sent![1].Content.Should().Contain("User: is terraria up?")
            .And.Contain("Assistant: Yes, terraria-1 is running.");
    }

    [Fact]
    public async Task ModelFailure_ReturnsFailure_AndAddsNoCheckpoint()
    {
        SeedTurn("a", "b");
        SeedTurn("c", "d");
        ScriptModel(Result.Failure<LlmResponse>("ollama unreachable"));

        var result = await Create().CompactAsync(Conversation);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("ollama unreachable");
        TurnCount().Should().Be(2);
        CheckpointCount().Should().Be(0);   // history untouched
    }

    [Fact]
    public async Task EmptyModelSummary_Fails_AndAddsNoCheckpoint()
    {
        SeedTurn("a", "b");
        SeedTurn("c", "d");
        ScriptModel(Result.Success(LlmResponse.Text("   ")));

        var result = await Create().CompactAsync(Conversation);

        result.IsFailure.Should().BeTrue();
        TurnCount().Should().Be(2);
        CheckpointCount().Should().Be(0);   // history untouched
    }
}
