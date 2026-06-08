namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The role of a participant in an LLM conversation.
/// </summary>
public enum LlmRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// A single message in an LLM conversation. Transport-agnostic so it can be
/// reused across backends. Carries optional tool-calling fields:
/// <see cref="ToolCalls"/> on an assistant turn that requests tool execution,
/// and <see cref="ToolName"/> on a tool-result turn.
/// </summary>
public record LlmMessage(LlmRole Role, string Content)
{
    /// <summary>Tool calls requested by the model (assistant turns only).</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>The tool this message carries the result of (tool turns only).</summary>
    public string? ToolName { get; init; }

    public static LlmMessage System(string content) => new(LlmRole.System, content);
    public static LlmMessage User(string content) => new(LlmRole.User, content);
    public static LlmMessage Assistant(string content) => new(LlmRole.Assistant, content);

    /// <summary>An assistant turn that requests one or more tool calls.</summary>
    public static LlmMessage AssistantToolCalls(IReadOnlyList<LlmToolCall> toolCalls) =>
        new(LlmRole.Assistant, string.Empty) { ToolCalls = toolCalls };

    /// <summary>A tool-result turn feeding output back to the model.</summary>
    public static LlmMessage Tool(string toolName, string content) =>
        new(LlmRole.Tool, content) { ToolName = toolName };
}
