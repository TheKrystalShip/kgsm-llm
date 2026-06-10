namespace TheKrystalShip.Llm.Models;

/// <summary>The kind of event emitted while streaming an agent turn.</summary>
public enum AgentEventKind
{
    /// <summary>A non-token progress note (e.g. a tool round is running).</summary>
    Status,

    /// <summary>An incremental slice of the final assistant reply text.</summary>
    Token,

    /// <summary>The turn is complete; <see cref="AgentEvent.Text"/> holds the full final reply.</summary>
    Final,

    /// <summary>The turn failed; <see cref="AgentEvent.ErrorMessage"/> explains why. Terminal.</summary>
    Error
}

/// <summary>
/// A single event from <c>ILlmAgent.RunStreamAsync</c>. The streaming analogue of the buffered
/// <c>Result&lt;string&gt;</c>: tokens arrive as they generate, tool rounds surface as
/// <see cref="AgentEventKind.Status"/>, and the stream ends with exactly one
/// <see cref="AgentEventKind.Final"/> or <see cref="AgentEventKind.Error"/>.
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string? Text = null, string? ErrorMessage = null)
{
    public static AgentEvent Status(string message) => new(AgentEventKind.Status, Text: message);
    public static AgentEvent Token(string delta) => new(AgentEventKind.Token, Text: delta);
    public static AgentEvent Final(string text) => new(AgentEventKind.Final, Text: text);
    public static AgentEvent Error(string error) => new(AgentEventKind.Error, ErrorMessage: error);
}
