using System.Runtime.CompilerServices;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Backends.Ollama;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Streaming counterpart to <see cref="LlmAgentTests"/>: drives <c>RunStreamAsync</c> with a fake
/// chunk-yielding <see cref="ILlmClient"/> and verifies the event sequence, tool-round dispatch,
/// and that conversation persistence is IDENTICAL to the buffered path.
/// </summary>
public class LlmAgentStreamTests
{
    private const string Conversation = "user-1:channel-2";

    private readonly IToolDispatcher _dispatcher = Substitute.For<IToolDispatcher>();
    private readonly TestConversationStore _store = new();

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
    private static LlmStreamChunk Thinking(string delta) => new(null, null, false, null, delta);
    private static LlmStreamChunk ToolFrame(params string[] names) =>
        new(null, names.Select(n => new LlmToolCall(new Tool(n), new Dictionary<string, string?>())).ToArray(), false);
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
    public async Task ToolRound_ThenProse_EmitsToolStartThenResult_DispatchesTool_AndReplaysTheCall()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("tool_a"), DoneFrame() },   // round 1: a tool call
            new[] { Content("all done"), DoneFrame() },   // round 2: the final prose
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Select(e => e.Kind).Should().Equal(
            AgentEventKind.ToolStart, AgentEventKind.ToolResult, AgentEventKind.Token, AgentEventKind.Final);
        events[0].ToolName.Should().Be(new Tool("tool_a"));
        events[1].ToolName.Should().Be(new Tool("tool_a"));
        events[1].ToolSummary.Should().Be("Done.");   // the dispatcher's output, surfaced to the stream
        events[3].Text.Should().Be("all done");

        await _dispatcher.Received(1).ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == new Tool("tool_a")), Arg.Any<CancellationToken>());

        // Replay parity with the buffered loop: the call comes back, its output does not.
        _store.Messages.Select(m => m.Role).Should().Equal(
            LlmRole.User, LlmRole.Assistant, LlmRole.Tool, LlmRole.Assistant);
        _store.Messages[1].ToolCalls.Should().ContainSingle().Which.Name.Should().Be(new Tool("tool_a"));
        _store.Messages.Single(m => m.Role == LlmRole.Tool).Content.Should().NotContain("Done.");
    }

    /// <summary>
    /// A rejected reply buys the turn one more round. The rejected text is already on the consumer's
    /// screen, so it stays in the answer — with the amendment that qualifies it — ahead of what the
    /// re-prompted round produces, and the record says the same thing the stream did.
    /// </summary>
    [Fact]
    public async Task ARejectedReply_RunsAnotherRound_AndTheAnswerKeepsWhatWasAlreadyShown()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Content("I stopped it."), DoneFrame() },
            new[] { Content("Stopping it now."), DoneFrame() },
        });
        var reviewed = new List<string>();

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(
            Turn() with { ReviewReply = Reviewing(reviewed, once: true) }));

        reviewed.Should().Equal("I stopped it.", "Stopping it now.");
        var final = events.Single(e => e.Kind == AgentEventKind.Final);
        final.Text.Should().Be("I stopped it.[amended]Stopping it now.");
        // The amendment reaches a client that renders live tokens and never re-reads the final text.
        string.Concat(events.Where(e => e.Kind == AgentEventKind.Token).Select(e => e.Text))
            .Should().Be("I stopped it.[amended]Stopping it now.");
        _store.Messages.Last().Content.Should().Be("I stopped it.[amended]Stopping it now.");
    }

    /// <summary>
    /// One re-prompt per turn. A model that makes the same reply twice is not talked round by a third
    /// attempt, and a host that keeps asking must not be able to spin the loop.
    /// </summary>
    [Fact]
    public async Task ARejectedReply_IsRePromptedOnce_ThenTheAmendmentStands()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Content("I stopped it."), DoneFrame() },
            new[] { Content("I stopped it."), DoneFrame() },
            new[] { Content("never reached"), DoneFrame() },
        });
        var reviewed = new List<string>();

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(
            Turn() with { ReviewReply = Reviewing(reviewed, once: false) }));

        reviewed.Should().HaveCount(2);
        events.Single(e => e.Kind == AgentEventKind.Final).Text
            .Should().Be("I stopped it.[amended]I stopped it.[amended]");
    }

    /// <summary>An accepted reply is untouched: no amendment, no extra round.</summary>
    [Fact]
    public async Task AnAcceptedReply_IsLeftExactlyAsWritten()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Content("all good"), DoneFrame() },
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(
            Turn() with { ReviewReply = _ => ReplyReview.Accept }));

        events.Single(e => e.Kind == AgentEventKind.Final).Text.Should().Be("all good");
    }

    /// <summary>
    /// A review that asks to retry while <paramref name="once"/> is true accepts the second reply;
    /// otherwise it keeps asking, which is what proves the loop's own cap.
    /// </summary>
    private static Func<string, ReplyReview> Reviewing(List<string> seen, bool once)
    {
        var asked = false;
        return text =>
        {
            seen.Add(text);
            if (once && asked)
                return ReplyReview.Accept;
            asked = true;
            return ReplyReview.Retry("call the tool", "[amended]");
        };
    }

    [Fact]
    public async Task ToolResult_CarriesDispatcherData_OpaquelyToTheStream()
    {
        // The opaque-card channel: a dispatcher returning ToolOutput(summary, data) surfaces `data` on
        // the matching ToolResult event (index-aligned by Task.WhenAll's input-order guarantee). The loop
        // never inspects it, and the model still only ever sees the summary string.
        var client = new ScriptedStreamClient(new[]
        {
            new[] { ToolFrame("run_health_check"), DoneFrame() },
            new[] { Content("ok"), DoneFrame() },
        });
        var agent = CreateAgent(client);

        var card = new { kind = "health", overall = "pass" };
        // Re-configure AFTER CreateAgent (whose default "Done." would otherwise win the last-config race).
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>())
            .Returns(new ToolOutput("Healthy.", card));

        var events = await DrainAsync(agent.RunStreamAsync(Turn()));

        var toolResult = events.Single(e => e.Kind == AgentEventKind.ToolResult);
        toolResult.ToolSummary.Should().Be("Healthy.");   // model-facing string (fed back into the conversation)
        toolResult.ToolData.Should().BeSameAs(card);      // opaque surface card (never shown to the model)
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
            .Should().Equal(new Tool("tool_a"), new Tool("tool_b"));
        events.Where(e => e.Kind == AgentEventKind.ToolResult).Select(e => e.ToolName)
            .Should().Equal(new Tool("tool_a"), new Tool("tool_b"));

        // Synthesised tool-call ids pair each start with its result (same index order) and are
        // unique — so a renderer can resolve "Reading…" pills even with two calls in one round.
        var startIds = events.Where(e => e.Kind == AgentEventKind.ToolStart).Select(e => e.ToolCallId).ToList();
        var resultIds = events.Where(e => e.Kind == AgentEventKind.ToolResult).Select(e => e.ToolCallId).ToList();
        startIds.Should().OnlyContain(id => !string.IsNullOrEmpty(id));
        startIds.Should().OnlyHaveUniqueItems();
        resultIds.Should().Equal(startIds, "each tool.result carries the SAME id as its paired tool.start");
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
        // Ids stay unique ACROSS rounds (the counter is turn-level, not per-round).
        events.Where(e => e.Kind == AgentEventKind.ToolStart).Select(e => e.ToolCallId)
            .Should().OnlyHaveUniqueItems();
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

    [Fact]
    public async Task ThinkingTurn_YieldsThinking_CapturesItInTheRecord_ButDoesNotReplayIt()
    {
        var client = new ScriptedStreamClient(new[]
        {
            new[] { Thinking("Let me think..."), Thinking(" more..."), Content("Answer"), DoneFrame() },
        });

        var events = await DrainAsync(CreateAgent(client).RunStreamAsync(Turn()));

        events.Select(e => e.Kind).Should().Equal(
            AgentEventKind.Thinking, AgentEventKind.Thinking,
            AgentEventKind.Token, AgentEventKind.Final);
        events[0].Text.Should().Be("Let me think...");
        events[1].Text.Should().Be(" more...");
        events[2].Text.Should().Be("Answer");
        events[3].Text.Should().Be("Answer");

        // Thinking IS captured in the turn record (the full reasoning, for analysis)...
        var record = _store.Turns.Should().ContainSingle().Subject;
        record.Thinking.Should().Be("Let me think... more...");
        // ...but it is NOT replayed into the model context — only user prompt + final assistant text.
        _store.Messages.Should().HaveCount(2);
        _store.Messages[0].Role.Should().Be(LlmRole.User);
        _store.Messages[1].Should().Match<LlmMessage>(m => m.Role == LlmRole.Assistant && m.Content == "Answer");
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
            bool think = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This fake only streams.");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            bool think = false,
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
}
