using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

public class LlmAgentTests
{
    private const string Conversation = "user-1:channel-2";

    private const string ToolA = "tool_a";
    private const string ToolB = "tool_b";
    private const string ToolC = "tool_c";

    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly IToolDispatcher _dispatcher = Substitute.For<IToolDispatcher>();
    private readonly TestConversationStore _store = new();
    private readonly List<List<LlmMessage>> _seen = new();

    private LlmAgent CreateAgent(int maxIterations = 8)
    {
        _dispatcher.ExecuteAsync(Arg.Any<LlmToolCall>(), Arg.Any<CancellationToken>()).Returns("Done.");
        var options = Options.Create(new LlmAgentOptions { MaxIterations = maxIterations });
        return new LlmAgent(_llm, _dispatcher, _store, options, NullLogger<LlmAgent>.Instance);
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
        new(new Tool(name), new Dictionary<string, string?> { ["instance_name"] = "x" });

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

    /// <summary>
    /// In a shared conversation the model is told who is speaking — and the record keeps the prompt
    /// exactly as it was typed. Both halves matter: the label is what stops the model conflating two
    /// people, and its absence from the record is what stops every later reader of the log inheriting
    /// a sentence nobody wrote.
    /// </summary>
    [Fact]
    public async Task ASpeaker_IsNamedToTheModel_ButNeverRecorded()
    {
        ScriptLlm(Text("It crashed twice."));

        var result = await CreateAgent().RunAsync(Turn() with { Speaker = "Alice" });

        result.IsSuccess.Should().BeTrue();
        _seen[0].Last().Content.Should().Be("Alice: do the thing");
        _store.Turns.Should().ContainSingle().Which.UserPrompt.Should().Be("do the thing");
    }

    /// <summary>
    /// Without a speaker the model-facing prompt is untouched — every one-participant conversation
    /// keeps reading exactly as it did.
    /// </summary>
    [Fact]
    public async Task WithNoSpeaker_ThePromptReachesTheModelUnlabelled()
    {
        ScriptLlm(Text("done"));

        await CreateAgent().RunAsync(Turn());

        _seen[0].Last().Content.Should().Be("do the thing");
    }

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
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolA)), Arg.Any<CancellationToken>());
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolB)), Arg.Any<CancellationToken>());
            _ = _dispatcher.ExecuteAsync(Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolC)), Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// A replayed turn keeps its shape — prompt, the call it made, the reply — because the transcript
    /// is also the examples the model imitates, and a turn that called a tool replayed as prose alone
    /// teaches that the same request is answered by describing the action instead of taking it.
    /// </summary>
    [Fact]
    public async Task AReplayedTurn_CarriesTheCallItMade()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("Finished."));

        await CreateAgent().RunAsync(Turn());

        _store.Messages.Select(m => m.Role).Should().Equal(
            LlmRole.User, LlmRole.Assistant, LlmRole.Tool, LlmRole.Assistant);
        _store.Messages[1].ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(new Tool(ToolA));
        _store.Messages[3].Content.Should().Be("Finished.");
    }

    /// <summary>
    /// The call replays; its OUTPUT does not. Each result is a reading of a world that has moved on,
    /// and a stale reading offered as current is a fabricated status — so the placeholder says the
    /// result is absent and asks for a fresh call.
    /// </summary>
    [Fact]
    public async Task AReplayedCall_CarriesNoStaleOutput()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("Finished."));

        await CreateAgent().RunAsync(Turn());

        var toolMessage = _store.Messages.Single(m => m.Role == LlmRole.Tool);
        toolMessage.Content.Should().NotContain("Done.");
        toolMessage.Content.Should().Contain("not replayed");
        // The record keeps the real output — the projection is what withholds it.
        _store.Turns.Should().ContainSingle()
            .Which.Tools.Should().ContainSingle().Which.Summary.Should().Be("Done.");
    }

    /// <summary>
    /// On the buffered path the rejected reply has been shown to nobody — it exists only inside the
    /// call — so the re-prompted answer replaces it outright rather than carrying it along.
    /// </summary>
    [Fact]
    public async Task ARejectedReply_IsDropped_AndTheRePromptedOneIsTheAnswer()
    {
        ScriptLlm(Text("I stopped it."), Text("Stopping it now."));
        var asked = false;

        ReplyReview Review(string _)
        {
            if (asked)
                return ReplyReview.Accept;
            asked = true;
            return ReplyReview.Retry("call the tool", "[amended]");
        }

        var result = await CreateAgent().RunAsync(Turn() with { ReviewReply = Review });

        result.Value!.Text.Should().Be("Stopping it now.");
        _store.Messages.Last().Content.Should().Be("Stopping it now.");
        // The nudge and the reply it refers to are what the second round reads.
        _seen.Last().Should().Contain(m => m.Role == LlmRole.Assistant && m.Content == "I stopped it.");
        _seen.Last().Last().Content.Should().Be("call the tool");
    }

    /// <summary>An amendment on an accepted reply is appended to the answer and to the record.</summary>
    [Fact]
    public async Task AnAmendedReply_CarriesTheAmendment()
    {
        ScriptLlm(Text("I stopped it."));

        var result = await CreateAgent().RunAsync(Turn() with
        {
            ReviewReply = _ => ReplyReview.Amend(" [correction]"),
        });

        result.Value!.Text.Should().Be("I stopped it. [correction]");
        _store.Messages.Last().Content.Should().Be("I stopped it. [correction]");
    }

    [Fact]
    public async Task NullGate_DispatchesEveryCall()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("ok"));

        await CreateAgent().RunAsync(Turn(gate: null));

        await _dispatcher.Received(1).ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolA)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GateRefusal_SkipsDispatch_AndFeedsRefusalBackToModel()
    {
        ScriptLlm(Tools(Call(ToolA)), Text("understood"));

        // Refuse ToolA with a specific message.
        var gate = (LlmToolCall c) =>
            c.Name == new Tool(ToolA) ? ToolGate.Refuse("nope, not allowed") : ToolGate.Allow;

        var result = await CreateAgent().RunAsync(Turn(gate: gate));

        result.IsSuccess.Should().BeTrue();
        // The refused call must never reach the dispatcher.
        await _dispatcher.DidNotReceive().ExecuteAsync(
            Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolA)), Arg.Any<CancellationToken>());
        // The refusal string must be fed back as a tool result on the next model call.
        var lastCallMessages = _seen.Last();
        lastCallMessages.Should().Contain(m =>
            m.Role == LlmRole.Tool && m.ToolName == new Tool(ToolA) && m.Content == "nope, not allowed");
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
            Arg.Is<LlmToolCall>(c => c.Name == new Tool(ToolA)), Arg.Any<CancellationToken>());
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public async Task EmptyReply_IsAnsweredAndRecordedAsEmpty_NotAsSuccess(string? content)
    {
        // A backend can end a generation having written nothing — it exhausts the context, or spends
        // the whole budget reasoning. Handing that on as an empty string reaches a person as silence.
        ScriptLlm(Result.Success(new LlmResponse(content, Array.Empty<LlmToolCall>())));

        var result = await CreateAgent().RunAsync(Turn());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Text.Should().Be(new LlmAgentOptions().EmptyReplyReply);

        var recorded = _store.Turns.Should().ContainSingle().Which;
        recorded.Outcome.Should().Be(TurnOutcome.Empty);
        recorded.Final.Should().Be(new LlmAgentOptions().EmptyReplyReply);
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
}
