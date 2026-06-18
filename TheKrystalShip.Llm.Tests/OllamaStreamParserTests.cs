using FluentAssertions;

using TheKrystalShip.Llm.Ollama;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Frame-shape tests for <see cref="OllamaStreamParser.ParseFrame"/>, grounded in the exact NDJSON
/// shapes observed from a live <c>gemma4:12b</c> stream: prose turns emit content deltas, a
/// tool-calling turn emits the full tool_calls in one non-final frame, and the stream ends in a
/// separate empty done frame.
/// </summary>
public class OllamaStreamParserTests
{
    [Fact]
    public void ContentDeltaFrame_IsParsedVerbatim_NotTrimmed()
    {
        // Leading space matters: trimming per-delta would weld tokens together.
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","content":" world"},"done":false}""");

        chunk.Should().NotBeNull();
        chunk!.ContentDelta.Should().Be(" world");
        chunk.ToolCalls.Should().BeNull();
        chunk.Done.Should().BeFalse();
    }

    [Fact]
    public void ToolCallFrame_CarriesCompleteCallsAndNoContent()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_server_status","arguments":{"instance":"terraria"}}}]},"done":false}
            """);

        chunk.Should().NotBeNull();
        chunk!.ContentDelta.Should().BeNull("an empty content string is not a delta");
        chunk.Done.Should().BeFalse();
        chunk.ToolCalls.Should().ContainSingle();
        chunk.ToolCalls![0].Name.Should().Be("get_server_status");
        chunk.ToolCalls[0].Arg("instance").Should().Be("terraria");
    }

    [Fact]
    public void TerminalFrame_HasDoneAndNoPayload()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""");

        chunk.Should().NotBeNull();
        chunk!.Done.Should().BeTrue();
        chunk.ContentDelta.Should().BeNull();
        chunk.ToolCalls.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ this is : broken")]
    public void BlankOrMalformedLine_ReturnsNull(string line)
    {
        OllamaStreamParser.ParseFrame(line).Should().BeNull();
    }

    [Fact]
    public void StringifiedToolArguments_AreUnwrapped()
    {
        // Some models emit `arguments` as a JSON string rather than an object.
        var chunk = OllamaStreamParser.ParseFrame(
            """
            {"message":{"tool_calls":[{"function":{"name":"start_server","arguments":"{\"instance\":\"valheim\"}"}}]},"done":false}
            """);

        chunk!.ToolCalls.Should().ContainSingle();
        chunk.ToolCalls![0].Arg("instance").Should().Be("valheim");
    }

    [Fact]
    public void DoneFrame_CarriesTokenUsage_StampedWithContextWindow()
    {
        // The terminal frame reports prompt_eval_count / eval_count; num_ctx is stamped by the caller.
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":1720,"eval_count":130}""",
            contextWindow: 32768);

        chunk!.Done.Should().BeTrue();
        chunk.Usage.Should().NotBeNull();
        chunk.Usage!.PromptTokens.Should().Be(1720);
        chunk.Usage.ResponseTokens.Should().Be(130);
        chunk.Usage.UsedTokens.Should().Be(1850);
        chunk.Usage.ContextWindow.Should().Be(32768);
        chunk.Usage.RemainingTokens.Should().Be(32768 - 1850);
    }

    [Fact]
    public void NonFinalFrame_WithoutCounts_HasNoUsage()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","content":"hi"},"done":false}""", contextWindow: 32768);

        chunk!.Usage.Should().BeNull();
    }

    [Fact]
    public void ThinkFrame_ParsesThinkingDelta()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","think":"Let me analyze this step by step.","content":""},"done":false}""");

        chunk.Should().NotBeNull();
        chunk!.ThinkingDelta.Should().Be("Let me analyze this step by step.");
        chunk.ContentDelta.Should().BeNull();
        chunk.Done.Should().BeFalse();
    }

    [Fact]
    public void ThinkAndContentFrame_ParsesBothFields()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","think":"reasoning...","content":" final answer"},"done":false}""");

        chunk.Should().NotBeNull();
        chunk!.ThinkingDelta.Should().Be("reasoning...");
        chunk.ContentDelta.Should().Be(" final answer");
    }

    [Fact]
    public void FrameWithoutThink_HasNullThinkingDelta()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","content":"hello"},"done":false}""");

        chunk.Should().NotBeNull();
        chunk!.ThinkingDelta.Should().BeNull();
    }

    [Fact]
    public void EmptyThinkField_ReturnsNullThinkingDelta()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","think":"","content":"hi"},"done":false}""");

        chunk.Should().NotBeNull();
        chunk!.ThinkingDelta.Should().BeNull("an empty think string is not a delta");
    }

    [Fact]
    public void DoneFrame_WithThink_CarriesThinkingDelta()
    {
        var chunk = OllamaStreamParser.ParseFrame(
            """{"message":{"role":"assistant","think":"final reasoning","content":""},"done":true,"done_reason":"stop"}""");

        chunk.Should().NotBeNull();
        chunk!.Done.Should().BeTrue();
        chunk.ThinkingDelta.Should().Be("final reasoning");
    }
}
