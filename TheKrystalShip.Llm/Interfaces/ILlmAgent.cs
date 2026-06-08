using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Runs a full agent turn: prepends the host-built system prompt, drives the
/// model↔tool loop (honoring the per-call gate), persists the conversation, and
/// returns the final reply text. Stateful only through the conversation store;
/// safe to share as a singleton.
/// </summary>
public interface ILlmAgent
{
    Task<Result<string>> RunAsync(AgentTurn turn, CancellationToken cancellationToken = default);
}
