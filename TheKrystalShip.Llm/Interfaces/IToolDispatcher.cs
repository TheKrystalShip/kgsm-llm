using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Executes a tool call requested by the model and returns a string result to
/// feed back into the conversation. Implementations must never throw: execution
/// failures (unknown tool, bad arguments, backend errors) are returned as result
/// strings so the model can recover within the agent loop.
///
/// NOTE: this layer is expected to enforce the tool whitelist (refuse unknown
/// tools) but NOT authorization or rate limits. Those are the host's policy,
/// supplied per turn via <see cref="AgentTurn.Gate"/> and evaluated by the agent
/// loop before any call reaches this dispatcher.
/// </summary>
public interface IToolDispatcher
{
    Task<string> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default);
}
