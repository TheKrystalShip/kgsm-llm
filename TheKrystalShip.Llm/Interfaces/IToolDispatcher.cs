using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Executes a tool call requested by the model and returns a <see cref="ToolOutput"/> to feed
/// back into the conversation: its <see cref="ToolOutput.Summary"/> is the model-facing result
/// string, plus an optional surface-facing <see cref="ToolOutput.Data"/> card the loop carries
/// opaquely to a streaming consumer (never shown to the model). Implementations must never throw:
/// execution failures (unknown tool, bad arguments, backend errors) are returned as result
/// summaries so the model can recover within the agent loop.
///
/// A summary-only tool can still <c>return "…";</c> directly — <see cref="ToolOutput"/>'s implicit
/// <see cref="string"/> conversion keeps those call sites unchanged.
///
/// NOTE: this layer is expected to enforce the tool whitelist (refuse unknown
/// tools) but NOT authorization or rate limits. Those are the host's policy,
/// supplied per turn via <see cref="AgentTurn.Gate"/> and evaluated by the agent
/// loop before any call reaches this dispatcher.
/// </summary>
public interface IToolDispatcher
{
    Task<ToolOutput> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default);
}
