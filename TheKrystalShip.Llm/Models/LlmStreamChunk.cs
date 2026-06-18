namespace TheKrystalShip.Llm.Models;

/// <summary>
/// One frame of a streamed model response. A backend emits these incrementally; a frame may
/// carry a slice of the reply text (<see cref="ContentDelta"/>), a complete set of tool calls
/// (<see cref="ToolCalls"/>), the terminal <see cref="Done"/> marker, or any combination.
/// <para>
/// Observed with Ollama + gemma: a tool-calling turn emits the full <see cref="ToolCalls"/> in a
/// single non-final frame followed by a separate <see cref="Done"/> frame; a prose turn streams
/// <see cref="ContentDelta"/> token-by-token ending in an empty <see cref="Done"/> frame. The
/// agent loop accumulates content deltas and captures tool calls until it sees <see cref="Done"/>.
/// </para>
/// </summary>
public sealed record LlmStreamChunk(
    string? ContentDelta,
    IReadOnlyList<LlmToolCall>? ToolCalls,
    bool Done,
    LlmUsage? Usage = null,
    string? ThinkingDelta = null);
