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
}
