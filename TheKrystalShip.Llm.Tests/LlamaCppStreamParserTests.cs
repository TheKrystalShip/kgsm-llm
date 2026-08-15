using System.Text.Json;

using FluentAssertions;

using TheKrystalShip.Llm.Backends.LlamaCpp;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Frame-shape tests for <see cref="LlamaCppStreamParser"/>, grounded in the OpenAI-compatible SSE
/// shapes llama-server emits. The parser's whole reason to be stateful is here: arguments arrive as
/// string fragments spread over frames, and the assembled call may only be emitted once the choice
/// reports a finish reason.
/// </summary>
public class LlamaCppStreamParserTests
{
    private const int ContextWindow = 32768;

    private static LlamaCppStreamParser Parser() => new(ContextWindow);

    [Fact]
    public void ContentDeltaFrame_IsParsedVerbatim_NotTrimmed()
    {
        // Leading space matters: trimming per-delta would weld tokens together.
        var chunk = Parser().ParseFrame(
            """data: {"choices":[{"delta":{"content":" world"},"finish_reason":null}]}""");

        chunk.Should().NotBeNull();
        chunk!.ContentDelta.Should().Be(" world");
        chunk.ToolCalls.Should().BeNull();
        chunk.Done.Should().BeFalse();
    }

    [Fact]
    public void NonDataLines_AreIgnored()
    {
        var parser = Parser();

        parser.ParseFrame("").Should().BeNull();
        parser.ParseFrame(": keep-alive").Should().BeNull("an SSE comment carries nothing");
        parser.ParseFrame("event: message").Should().BeNull("only the data field is ours to read");
        parser.ParseFrame("data: {not json").Should().BeNull();
    }

    [Fact]
    public void ReasoningContent_SurfacesAsThinkingDelta()
    {
        var chunk = Parser().ParseFrame(
            """data: {"choices":[{"delta":{"reasoning_content":"weighing it up"},"finish_reason":null}]}""");

        chunk.Should().NotBeNull();
        chunk!.ThinkingDelta.Should().Be("weighing it up");
        chunk.ContentDelta.Should().BeNull();
    }

    [Fact]
    public void ToolCallArguments_SplitAcrossFrames_AreAssembledOnFinish()
    {
        var parser = Parser();

        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_server_status","arguments":""}}]},"finish_reason":null}]}""")
            .Should().BeNull("a partial tool call is not yet anything a consumer can act on");

        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"instance\":\"terr"}}]},"finish_reason":null}]}""")
            .Should().BeNull();

        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"aria\"}"}}]},"finish_reason":null}]}""")
            .Should().BeNull();

        var chunk = parser.ParseFrame(
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        chunk.Should().NotBeNull();
        chunk!.Done.Should().BeFalse("tool calls ride a non-final frame, as they do on Ollama");
        chunk.ToolCalls.Should().ContainSingle();
        chunk.ToolCalls![0].Name.Name.Should().Be("get_server_status");
        chunk.ToolCalls[0].Arg("instance").Should().Be("terraria");
    }

    [Fact]
    public void WholeToolCallInOneFrame_IsAssembledIdentically()
    {
        var parser = Parser();

        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"list_instances","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            !.ToolCalls.Should().ContainSingle().Which.Name.Name.Should().Be("list_instances");
    }

    [Fact]
    public void SeveralToolCalls_AreOrderedByIndex()
    {
        var parser = Parser();

        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"name":"second","arguments":"{}"}}]},"finish_reason":null}]}""");
        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"first","arguments":"{}"}}]},"finish_reason":null}]}""");

        var chunk = parser.ParseFrame(
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        chunk!.ToolCalls!.Select(c => c.Name.Name).Should().Equal("first", "second");
    }

    [Fact]
    public void NonStringArgumentValues_AreCoercedToStrings()
    {
        var parser = Parser();

        var chunk = parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"open_port","arguments":"{\"port\":27015,\"enabled\":true}"}}]},"finish_reason":"tool_calls"}]}""");

        chunk!.ToolCalls![0].Arg("port").Should().Be("27015");
        chunk.ToolCalls[0].Arg("enabled").Should().Be("true");
    }

    [Fact]
    public void Usage_RidesTheTerminalFrame_NotTheFrameItArrivedOn()
    {
        var parser = Parser();

        parser.ParseFrame("""data: {"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""")
            !.Usage.Should().BeNull("only the terminal frame reports accounting");

        parser.ParseFrame("""data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        // The usage-bearing frame carries no choices at all and must not surface on its own.
        parser.ParseFrame("""data: {"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":8}}""")
            .Should().BeNull();

        var done = parser.ParseFrame("data: [DONE]");

        done.Should().NotBeNull();
        done!.Done.Should().BeTrue();
        done.Usage.Should().NotBeNull();
        done.Usage!.PromptTokens.Should().Be(120);
        done.Usage.ResponseTokens.Should().Be(8);
        done.Usage.ContextWindow.Should().Be(ContextWindow, "the response never echoes it back");
    }

    [Fact]
    public void Finish_ClosesAStreamThatEndedWithoutDone()
    {
        var parser = Parser();
        parser.ParseFrame("""data: {"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""");

        var terminal = parser.Finish();

        terminal.Should().NotBeNull();
        terminal!.Done.Should().BeTrue();
    }

    [Fact]
    public void Finish_IsNullOnceDoneWasAlreadyEmitted()
    {
        var parser = Parser();
        parser.ParseFrame("data: [DONE]")!.Done.Should().BeTrue();

        parser.Finish().Should().BeNull("a consumer must never see two terminal frames");
    }

    [Fact]
    public void ToolCallsNeverMarkedFinished_StillReachTheConsumerOnDone()
    {
        var parser = Parser();
        parser.ParseFrame(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"list_instances","arguments":"{}"}}]},"finish_reason":null}]}""");

        var done = parser.ParseFrame("data: [DONE]");

        done!.Done.Should().BeTrue();
        done.ToolCalls.Should().ContainSingle()
            .Which.Name.Name.Should().Be("list_instances", "a dropped call would be a silently lost action");
    }

    [Fact]
    public void ParseBuffered_ReadsContentToolCallsAndUsage()
    {
        using var document = JsonDocument.Parse(
            """
            {"choices":[{"message":{"role":"assistant","content":" done ","tool_calls":[
              {"id":"call_0","type":"function","function":{"name":"get_server_status","arguments":"{\"instance\":\"factorio\"}"}}
            ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":40,"completion_tokens":12}}
            """);

        var response = LlamaCppStreamParser.ParseBuffered(document.RootElement, ContextWindow);

        response.Should().NotBeNull();
        response!.Content.Should().Be("done", "the assembled whole is trimmed, unlike a delta");
        response.ToolCalls.Should().ContainSingle();
        response.ToolCalls[0].Arg("instance").Should().Be("factorio");
        response.Usage!.UsedTokens.Should().Be(52);
    }

    [Fact]
    public void ParseBuffered_ReturnsNullOnAnUnexpectedShape()
    {
        using var document = JsonDocument.Parse("""{"choices":[]}""");

        LlamaCppStreamParser.ParseBuffered(document.RootElement, ContextWindow).Should().BeNull();
    }

    [Fact]
    public void ParseUsage_IsNullWhenTheRequestDidNotAskForIt()
    {
        using var document = JsonDocument.Parse("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        LlamaCppStreamParser.ParseUsage(document.RootElement, ContextWindow).Should().BeNull();
    }
}
