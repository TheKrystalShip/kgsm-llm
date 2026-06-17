namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A single response from the model: either free-text content, one or more
/// tool calls, or both. <see cref="Usage"/> carries the call's token accounting
/// when the backend reports it (null otherwise).
/// </summary>
public record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, LlmUsage? Usage = null)
{
    public bool HasToolCalls => ToolCalls.Count > 0;

    public static LlmResponse Text(string? content) =>
        new(content, Array.Empty<LlmToolCall>());
}
