namespace TheKrystalShip.Llm.Models;

/// <summary>The kind of event emitted while streaming an agent turn.</summary>
public enum AgentEventKind
{
    /// <summary>An incremental slice of the final assistant reply text.</summary>
    Token,

    /// <summary>An incremental slice of the model's internal reasoning (thinking) content.</summary>
    Thinking,

    /// <summary>A tool is about to be dispatched; carries its name + arguments.</summary>
    ToolStart,

    /// <summary>A tool finished; carries its name + a summary of the result.</summary>
    ToolResult,

    /// <summary>The turn is complete; <see cref="AgentEvent.Text"/> holds the full final reply.</summary>
    Final,

    /// <summary>The turn failed; <see cref="AgentEvent.ErrorMessage"/> explains why. Terminal.</summary>
    Error
}

/// <summary>
/// A single event from <c>ILlmAgent.RunStreamAsync</c>. The streaming analogue of the buffered
/// <c>Result&lt;string&gt;</c>: tokens arrive as they generate; each tool round surfaces as one
/// <see cref="AgentEventKind.ToolStart"/> per call (before dispatch) then one
/// <see cref="AgentEventKind.ToolResult"/> per call (after); the stream ends with exactly one
/// <see cref="AgentEventKind.Final"/> or <see cref="AgentEventKind.Error"/>.
/// </summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Text = null,
    string? ErrorMessage = null,
    string? ToolName = null,
    IReadOnlyDictionary<string, string?>? ToolArguments = null,
    string? ToolSummary = null,
    LlmUsage? Usage = null)
{
    public static AgentEvent Token(string delta) => new(AgentEventKind.Token, Text: delta);
    public static AgentEvent Thinking(string delta) => new(AgentEventKind.Thinking, Text: delta);
    public static AgentEvent ToolStart(string tool, IReadOnlyDictionary<string, string?> arguments) =>
        new(AgentEventKind.ToolStart, ToolName: tool, ToolArguments: arguments);
    public static AgentEvent ToolResult(string tool, string summary) =>
        new(AgentEventKind.ToolResult, ToolName: tool, ToolSummary: summary);

    /// <summary>The terminal success event: the full reply plus the producing call's token usage (if any).</summary>
    public static AgentEvent Final(string text, LlmUsage? usage = null) =>
        new(AgentEventKind.Final, Text: text, Usage: usage);
    public static AgentEvent Error(string error) => new(AgentEventKind.Error, ErrorMessage: error);
}
