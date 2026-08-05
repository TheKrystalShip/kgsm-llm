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

    /// <summary>A tool finished; carries its name, a summary of the result, and an optional opaque card.</summary>
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
    Tool? ToolName = null,
    IReadOnlyDictionary<string, string?>? ToolArguments = null,
    string? ToolSummary = null,
    LlmUsage? Usage = null,
    string? ToolCallId = null,
    object? ToolData = null,
    long TurnId = 0)
{
    public static AgentEvent Token(string delta) => new(AgentEventKind.Token, Text: delta);
    public static AgentEvent Thinking(string delta) => new(AgentEventKind.Thinking, Text: delta);
    public static AgentEvent ToolStart(Tool tool, IReadOnlyDictionary<string, string?> arguments, string? id = null) =>
        new(AgentEventKind.ToolStart, ToolName: tool, ToolArguments: arguments, ToolCallId: id);
    // `data` is the dispatcher's optional surface-facing card (ToolOutput.Data), carried opaquely.
    public static AgentEvent ToolResult(Tool tool, string summary, string? id = null, object? data = null) =>
        new(AgentEventKind.ToolResult, ToolName: tool, ToolSummary: summary, ToolCallId: id, ToolData: data);

    /// <summary>
    /// The terminal success event: the full reply, the producing call's token usage (if any), and the
    /// id the turn was stored under. That id is what lets a surface act on the answer it just received
    /// — rating it, linking to it — instead of inferring which turn "the last one" was; it is <c>0</c>
    /// when the turn could not be persisted, which a surface reads as "not addressable".
    /// </summary>
    public static AgentEvent Final(string text, LlmUsage? usage = null, long turnId = 0) =>
        new(AgentEventKind.Final, Text: text, Usage: usage, TurnId: turnId);
    public static AgentEvent Error(string error) => new(AgentEventKind.Error, ErrorMessage: error);
}
