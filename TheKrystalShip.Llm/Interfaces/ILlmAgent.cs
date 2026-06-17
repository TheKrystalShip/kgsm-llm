using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Runs a full agent turn: prepends the host-built system prompt, drives the
/// model↔tool loop (honoring the per-call gate), persists the conversation, and
/// returns the final reply text plus the producing call's token usage. Stateful
/// only through the conversation store; safe to share as a singleton.
/// </summary>
public interface ILlmAgent
{
    Task<Result<AgentRunResult>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming counterpart to <see cref="RunAsync"/>: drives the same model↔tool loop but yields
    /// <see cref="AgentEvent"/>s as they happen — content <c>Token</c>s for the final reply,
    /// <c>Status</c> notes around tool rounds — ending with exactly one terminal <c>Final</c> (the
    /// full reply text) or <c>Error</c>. Conversation persistence is identical to
    /// <see cref="RunAsync"/>: only the user prompt and the final assistant text are stored.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunStreamAsync(AgentTurn turn, CancellationToken cancellationToken = default);
}
