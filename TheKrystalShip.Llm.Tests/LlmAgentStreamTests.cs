using System.Runtime.CompilerServices;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Streaming counterpart to <see cref="LlmAgentTests"/>: drives <c>RunStreamAsync</c> with a fake
/// chunk-yielding <see cref="ILlmClient"/> and verifies the event sequence, tool-round dispatch,
/// and that conversation persistence is IDENTICAL to the buffered path (only the user prompt and
/// the final assistant text are stored).
/// </summary>
public class LlmAgentStreamTests
{
    private const string Conversation = "user-1:channel-2";

    private readonly IToolDispatcher _dispatcher = Substitute.For<IToolDispatcher>();
    private readonly FakeConversationStore _store = new();

    private LlmAgent CreateAgent(ILlmClient client, int maxIterations = 8)
    {
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>()).Returns("Done.");
        var options = Options.Create(new LlmAgentOptions { MaxIterations = maxIterations });
        return new LlmAgent(client, _dispatcher, _store, options, NullLogger<LlmAgent>.Instance);
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
    private static LlmStreamChunk ToolFrame(params string[] names) =>
        new(null, names.Select(n => new LlmToolCall(n, new Dictionary<string, string?>())).ToArray(), false);
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
    public async Task ProseTurn_YieldsTokensThenFinal_AndPersistsFinalText()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Content("Hel"), Content("lo"), DoneFrame() },
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Select(e => e.Kind).Should().Equal(
            AgentEventKind.Token, AgentEventKind.Token, AgentEventKind.Final);
        events[0].Text.Should().Be("Hel");
        events[1].Text.Should().Be("lo");
        events[2].Text.Should().Be("Hello");

        _store.Messages.Should().HaveCount(2);
        _store.Messages[0].Role.Should().Be(LlmRole.User);
        _store.Messages[1].Should().Match<LlmMessage>(m => m.Role == LlmRole.Assistant && m.Content == "Hello");
    }

    [Fact]
    public async Task FinalEvent_CarriesUsageFromTheTerminalDoneFrame()
    {
        var usage = new LlmUsage(900, 60, 32768);
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Content("hi"), DoneFrame(usage) },
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        var final = events.Single(e => e.Kind == AgentEventKind.Final);
        final.Usage.Should().Be(usage);
        final.Usage!.UsedTokens.Should().Be(960);
    }

    [Fact]
    public async Task ToolRound_ThenProse_EmitsToolStartThenResult_DispatchesTool_AndPersistsOnlyFinalText()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("tool_a"), DoneFrame() },   // round 1: a tool call
            new[] { Content("all done"), DoneFrame() },   // round 2: the final prose
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Select(e => e.Kind).Should().Equal(
            AgentEventKind.ToolStart, AgentEventKind.ToolResult, AgentEventKind.Token, AgentEventKind.Final);
        events[0].ToolName.Should().Be("tool_a");
        events[1].ToolName.Should().Be("tool_a");
        events[1].ToolSummary.Should().Be("Done.");   // the dispatcher's output, surfaced to the stream
        events[3].Text.Should().Be("all done");

        await _dispatcher.Received(1).ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == "tool_a"), Arg.Any<CancellationToken>());

        // Persistence parity with the buffered loop: no tool messages, no tool-call turns.
        _store.Messages.Should().HaveCount(2);
        _store.Messages.Should().NotContain(m => m.Role == LlmRole.Tool);
        _store.Messages.Should().NotContain(m => m.ToolCalls != null);
    }

    [Fact]
    public async Task MultiToolRound_EmitsAllStartsThenAllResults_InInputOrder()
    {
        // The §5a ordering invariant: all tool.start (input order) BEFORE dispatch, then all
        // tool.result (input order). Two tools in one round make the batching observable.
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("tool_a", "tool_b"), DoneFrame() },
            new[] { Content("ok"), DoneFrame() },
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Select(e => e.Kind).Should().Equal(
            AgentEventKind.ToolStart, AgentEventKind.ToolStart,
            AgentEventKind.ToolResult, AgentEventKind.ToolResult,
            AgentEventKind.Token, AgentEventKind.Final);
        events.Where(e => e.Kind == AgentEventKind.ToolStart).Select(e => e.ToolName)
            .Should().Equal("tool_a", "tool_b");
        events.Where(e => e.Kind == AgentEventKind.ToolResult).Select(e => e.ToolName)
            .Should().Equal("tool_a", "tool_b");
    }

    [Fact]
    public async Task IterationCap_YieldsConfiguredReplyAsFinal()
    {
        // Model keeps requesting tools; loop must bail at MaxIterations with the configured reply.
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("tool_a"), DoneFrame() },
            new[] { ToolFrame("tool_a"), DoneFrame() },
            new[] { ToolFrame("tool_a"), DoneFrame() },
        });

        var events = await DrainAsync(CreateAgent(client, maxIterations: 3).RunStreamAsync(Turn()));

        events[^1].Kind.Should().Be(AgentEventKind.Final);
        events[^1].Text.Should().Be(new LlmAgentOptions().IterationLimitReply);
        // 3 rounds, one tool each → a start+result pair per round.
        events.Count(e => e.Kind == AgentEventKind.ToolStart).Should().Be(3);
        events.Count(e => e.Kind == AgentEventKind.ToolResult).Should().Be(3);
    }

    [Fact]
    public async Task FailedStream_YieldsSingleErrorEvent()
    {
        var client = new ScriptedStreamClient(Array.Empty<LlmStreamChunk[]>(), throwMessage: "backend down");

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(AgentEventKind.Error);
        events[0].ErrorMessage.Should().Be("backend down");
    }

    /// <summary>
    /// A fake <see cref="ILlmClient"/> that yields a scripted chunk sequence per
    /// <c>ChatStreamAsync</c> call, optionally throwing (mid-stream backend failure).
    /// </summary>
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
