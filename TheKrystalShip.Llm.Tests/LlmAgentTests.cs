using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Recording;

namespace TheKrystalShip.Llm.Tests;

public class LlmAgentTests
{
    private const string Conversation = "user-1:channel-2";

    private const string ToolA = "tool_a";
    private const string ToolB = "tool_b";
    private const string ToolC = "tool_c";

    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly IToolDispatcher _dispatcher = Substitute.For<IToolDispatcher>();
    private readonly FakeConversationStore _store = new();
    private readonly List<List<LlmMessage>> _seen = new();

    private LlmAgent CreateAgent(int maxIterations = 8)
    {
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>()).Returns("Done.");
        var options = Options.Create(new LlmAgentOptions { MaxIterations = maxIterations });
        return new LlmAgent(_llm, _dispatcher, _store, new NoopConversationRecorder(), options, NullLogger<LlmAgent>.Instance);
    }

    private void ScriptLlm(params Result<LlmResponse>[] responses) =>
        _llm.ChatAsync(
                Arg.Do<IReadOnlyList<LlmMessage>>(m => _seen.Add(m.ToList())),
                Arg.Any<IReadOnlyList<LlmToolDefinition>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(responses[0], responses.Skip(1).ToArray());

    private void ScriptLlmAlways(Func<Result<LlmResponse>> factory) =>
        _llm.ChatAsync(
                Arg.Do<IReadOnlyList<LlmMessage>>(m => _seen.Add(m.ToList())),
                Arg.Any<IReadOnlyList<LlmToolDefinition>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => factory());

    private static Result<LlmResponse> Tools(params LlmToolCall[] calls) =>
        Result.Success(new LlmResponse(null, calls));

    private static Result<LlmResponse> Text(string text) =>
        Result.Success(new LlmResponse(text, Array.Empty<LlmToolCall>()));

    private static LlmToolCall Call(string name) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = "x" });

    private static AgentTurn Turn(
        IReadOnlyList<LlmToolDefinition>? tools = null,
        Func<LlmToolCall, ToolGate>? gate = null) =>
        new()
        {
            ConversationId = Conversation,
            UserPrompt = "do the thing",
            SystemPrompt = "system",
            Tools = tools ?? Array.Empty<LlmToolDefinition>(),
            Gate = gate,
        };

    [Fact]
    public async Task MultiToolCalls_AreDispatchedSequentiallyInOrder()
    {
        ScriptLlm(
            Tools(Call(ToolA)),
            Tools(Call(ToolB)),
            Tools(Call(ToolC)),
            Text("all done"));

        var result = await CreateAgent().RunAsync(Turn());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Text.Should().Be("all done");
        Received.InOrder(() =>
        {
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == ToolA), Arg.Any<CancellationToken>());
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == ToolB), Arg.Any<CancellationToken>());
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == ToolC), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PersistenceBoundary_StoresOnlyUserAndFinalAssistantText()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("Finished."));

        await CreateAgent().RunAsync(Turn());

        _store.Messages.Should().HaveCount(2);
        _store.Messages[0].Role.Should().Be(LlmRole.User);
        _store.Messages[1].Role.Should().Be(LlmRole.Assistant);
        _store.Messages[1].Content.Should().Be("Finished.");
        _store.Messages.Should().NotContain(m => m.Role == LlmRole.Tool);
        _store.Messages.Should().NotContain(m => m.ToolCalls != null);
    }

    [Fact]
    public async Task NullGate_DispatchesEveryCall()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("ok"));

        await CreateAgent().RunAsync(Turn(gate: null));

        await _dispatcher.Received(1).ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == ToolA), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GateRefusal_SkipsDispatch_AndFeedsRefusalBackToModel()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("understood"));

        // Refuse ToolA with a specific message.
        var gate = (LlmToolCall c) =>
            c.Name == ToolA ? ToolGate.Refuse("nope, not allowed") : ToolGate.Allow;

        var result = await CreateAgent().RunAsync(Turn(gate: gate));

        result.IsSuccess.Should().BeTrue();
        // The refused call must never reach the dispatcher.
        await _dispatcher.DidNotReceive().ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == ToolA), Arg.Any<CancellationToken>());
        // The refusal string must be fed back as a tool result on the next model call.
        var lastCallMessages = _seen.Last();
        lastCallMessages.Should().Contain(m =>
            m.Role == LlmRole.Tool && m.ToolName == ToolA && m.Content == "nope, not allowed");
    }

    [Fact]
    public async Task GateCounterClosure_EnforcesAHostDefinedCap()
    {
        // Six tool calls in one assistant turn; host gate caps allowed dispatches at 2.
        var six = Enumerable.Range(0, 6).Select(_ => Call(ToolA)).ToArray();
        ScriptLlm(Tools(six), Text("done"));

        var allowed = 0;
        var gate = (LlmToolCall _) =>
        {
            if (allowed >= 2) return ToolGate.Refuse("cap reached");
            allowed++;
            return ToolGate.Allow;
        };

        await CreateAgent().RunAsync(Turn(gate: gate));

        await _dispatcher.Received(2).ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == ToolA), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IterationCap_ReturnsConfiguredReply_WhenModelNeverStops()
    {
        // Model keeps requesting tools forever; loop must bail at MaxIterations.
        ScriptLlmAlways(() => Tools(Call(ToolA)));

        var result = await CreateAgent(maxIterations: 3).RunAsync(Turn());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Text.Should().Be(new LlmAgentOptions().IterationLimitReply);
    }

    [Fact]
    public async Task BufferedResult_CarriesUsageFromTheFinalResponse()
    {
        var usage = new LlmUsage(1720, 130, 32768);
        ScriptLlm(Result.Success(new LlmResponse("done", Array.Empty<LlmToolCall>(), usage)));

        var result = await CreateAgent().RunAsync(Turn());

        result.Value!.Text.Should().Be("done");
        result.Value.Usage.Should().Be(usage);
        result.Value.Usage!.UsedTokens.Should().Be(1850);
    }

    [Fact]
    public async Task LlmFailure_PropagatesAsFailure()
    {
        ScriptLlm(Result.Failure<LlmResponse>("backend down"));

        var result = await CreateAgent().RunAsync(Turn());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("backend down");
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
