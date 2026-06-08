namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A single response from the model: either free-text content, one or more
/// tool calls, or both.
/// </summary>
public record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;

    public static LlmResponse Text(string? content) =>
        new(content, Array.Empty<LlmToolCall>());
}
