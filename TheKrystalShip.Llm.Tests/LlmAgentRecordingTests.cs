using System.Runtime.CompilerServices;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The recording side-channel: every turn the agent runs is captured as exactly one append-only
/// <see cref="ConversationTurnRecord"/> at its terminal point, across all four outcomes (ok / error /
/// cap-hit / cancelled), with the full tool trajectory (args + RAW pre-truncation result). A disabled
/// recorder must capture nothing — the loop short-circuits on <see cref="IConversationRecorder.Enabled"/>.
/// </summary>
public class LlmAgentRecordingTests
{
    private const string Conversation = "cli:abc";

    private readonly IToolDispatcher _dispatcher = Substitute.For<IToolDispatcher>();
    private readonly FakeConversationStore _store = new();
    private readonly CapturingRecorder _recorder = new();

    private LlmAgent CreateAgent(ILlmClient client, int maxIterations = 8, IConversationRecorder? recorder = null)
    {
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>()).Returns("Done.");
        var options = Options.Create(new LlmAgentOptions { MaxIterations = maxIterations });
        return new LlmAgent(client, _dispatcher, _store, recorder ?? _recorder, options, NullLogger<LlmAgent>.Instance);
    }

    private static AgentTurn Turn() => new()
    {
        ConversationId = Conversation,
        UserPrompt = "do the thing",
        SystemPrompt = "system",
        Tools = Array.Empty<LlmToolDefinition>(),
        Gate = null,
    };

    private static LlmStreamChunk Content(string delta) => new(delta, null, false);
    private static LlmStreamChunk ToolFrame(string name, Dictionary<string, string?> args) =>
        new(null, new[] { new LlmToolCall(name, args) }, false);
    private static LlmStreamChunk DoneFrame() => new(null, null, true);
    private static LlmStreamChunk DoneFrame(LlmUsage usage) => new(null, null, true, usage);

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var ev in events)
            collected.Add(ev);
        return collected;
    }

    [Fact]
    public async Task StreamingTurn_WithToolRound_RecordsOk_WithFullTrajectory()
    {
        var args = new Dictionary<string, string?> { ["instance"] = "terraria" };
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("get_status", args), DoneFrame() },               // round 1: tool
            new[] { Content("Terraria is up"), DoneFrame(new LlmUsage(1720, 130, 32768)) }, // round 2: prose
        });

        await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        var rec = _recorder.Records.Should().ContainSingle().Subject;
        rec.Outcome.Should().Be(TurnOutcome.Ok);
        rec.ConversationId.Should().Be(Conversation);
        rec.UserPrompt.Should().Be("do the thing");
        rec.Final.Should().Be("Terraria is up");
        rec.Usage.Should().Be(new LlmUsage(1720, 130, 32768));
        rec.Iterations.Should().Be(2);
        rec.SystemPromptHash.Should().NotBeNullOrWhiteSpace();

        var tool = rec.Tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("get_status");
        tool.Arguments.Should().Contain("instance", "terraria");
        tool.Result.Should().Be("Done.");   // the RAW dispatcher output, captured for analysis
    }

    [Fact]
    public async Task StreamingTurn_Cancelled_RecordsCancelled_WithNullFinal()
    {
        // Simulate a real Ctrl-C: the token is cancelled mid-stream (as TurnInterruptor does), so the
        // generation throws an OCE with the token requested — the finally must label this Cancelled.
        using var cts = new CancellationTokenSource();
        var client = new CancellingStreamClient(cts);

        var drain = async () => await DrainAsync(CreateAgent(client).RunStreamAsync(Turn(), cts.Token));

        await drain.Should().ThrowAsync<OperationCanceledException>();
        var rec = _recorder.Records.Should().ContainSingle().Subject;
        rec.Outcome.Should().Be(TurnOutcome.Cancelled);
        rec.Final.Should().BeNull();
    }

    [Fact]
    public async Task StreamingTurn_ToolDispatchThrows_RecordsError_NotCancelled()
    {
        // The generic IToolDispatcher contract doesn't forbid throwing (the real ToolDispatcher
        // swallows, but the lib must not assume that). A non-token throw is an errored turn, NOT a
        // user bail-out — it must never pollute the "cancelled" bucket.
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("boom", new Dictionary<string, string?>()), DoneFrame() },
        });
        var agent = CreateAgent(client);
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tool blew up"));

        var drain = async () => await DrainAsync(agent.RunStreamAsync(Turn()));

        await drain.Should().ThrowAsync<InvalidOperationException>();
        _recorder.Records.Should().ContainSingle().Which.Outcome.Should().Be(TurnOutcome.Error);
    }

    [Fact]
    public async Task StreamingTurn_BackendError_RecordsError_WithMessage()
    {
        var client = new ScriptedStreamClient(Array.Empty<LlmStreamChunk[]>(), throwMessage: "backend down");

        await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        var rec = _recorder.Records.Should().ContainSingle().Subject;
        rec.Outcome.Should().Be(TurnOutcome.Error);
        rec.Error.Should().Be("backend down");
        rec.Final.Should().BeNull();
    }

    [Fact]
    public async Task BufferedTurn_LlmFailure_RecordsError()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LlmResponse>("backend down"));

        var result = await CreateAgent(llm).RunAsync(Turn());

        result.IsFailure.Should().BeTrue();
        var rec = _recorder.Records.Should().ContainSingle().Subject;
        rec.Outcome.Should().Be(TurnOutcome.Error);
        rec.Error.Should().Be("backend down");
    }

    [Fact]
    public async Task BufferedTurn_IterationCap_RecordsCapHit_WithTrajectory()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmResponse(null, new[] { new LlmToolCall("tool_a", new Dictionary<string, string?>()) })));

        await CreateAgent(llm, maxIterations: 3).RunAsync(Turn());

        var rec = _recorder.Records.Should().ContainSingle().Subject;
        rec.Outcome.Should().Be(TurnOutcome.CapHit);
        rec.Iterations.Should().Be(3);
        rec.Tools.Should().HaveCount(3);   // one tool dispatched per round, three rounds
    }

    [Fact]
    public async Task DisabledRecorder_CapturesNothing()
    {
        var disabled = new CapturingRecorder(enabled: false);
        var llm = Substitute.For<ILlmClient>();
        llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmResponse("done", Array.Empty<LlmToolCall>())));

        await CreateAgent(llm, recorder: disabled).RunAsync(Turn());

        disabled.Records.Should().BeEmpty();   // Enabled=false ⇒ the loop never builds a record
    }

    private sealed class CapturingRecorder : IConversationRecorder
    {
        public List<ConversationTurnRecord> Records { get; } = new();
        public bool Enabled { get; }
        public CapturingRecorder(bool enabled = true) => Enabled = enabled;
        public void Record(ConversationTurnRecord record) => Records.Add(record);
    }

    private sealed class ScriptedStreamClient : ILlmClient
    {
        private readonly Queue<LlmStreamChunk[]> _scripts;
        private readonly string? _throwMessage;

        public ScriptedStreamClient(IEnumerable<LlmStreamChunk[]> scripts, string? throwMessage = null)
        {
            _scripts = new Queue<LlmStreamChunk[]>(scripts);
            _throwMessage = throwMessage;
        }

        public Task<Result<LlmResponse>> ChatAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This fake only streams.");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var script = _scripts.Count > 0 ? _scripts.Dequeue() : Array.Empty<LlmStreamChunk>();
            foreach (var chunk in script)
            {
                await Task.Yield();
                yield return chunk;
            }

            if (_throwMessage is not null)
                throw new OllamaBackendException(_throwMessage);
        }
    }

    /// <summary>
    /// Yields one token, then cancels the turn's token and observes it — the shape of a real Ctrl-C
    /// mid-generation (an OCE thrown with the token genuinely requested).
    /// </summary>
    private sealed class CancellingStreamClient : ILlmClient
    {
        private readonly CancellationTokenSource _cts;
        public CancellingStreamClient(CancellationTokenSource cts) => _cts = cts;

        public Task<Result<LlmResponse>> ChatAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This fake only streams.");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new LlmStreamChunk("partial", null, false);
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeConversationStore : IConversationStore
    {
        public List<LlmMessage> Messages { get; } = new();
        public IReadOnlyList<LlmMessage> GetHistory(string conversationId) => Messages.ToArray();
        public void Append(string conversationId, params LlmMessage[] messages) => Messages.AddRange(messages);
        public void Replace(string conversationId, params LlmMessage[] messages)
        {
            Messages.Clear();
            Messages.AddRange(messages);
        }
    }
}
