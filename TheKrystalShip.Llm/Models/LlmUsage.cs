namespace TheKrystalShip.Llm.Models;

/// <summary>
/// Token accounting for a single model call, reported by the backend. <see cref="PromptTokens"/>
/// is the assembled prompt the model evaluated this turn (system prompt + windowed history + the
/// user message); <see cref="ResponseTokens"/> is what it generated. Their sum
/// (<see cref="UsedTokens"/>) is how much of the <see cref="ContextWindow"/> the turn occupied —
/// the figure surfaces verbatim so callers can show "used / available" in tokens, never a
/// percentage.
/// <para>
/// This is per-turn occupancy, not a cumulative session total: the conversation is a rolling
/// window with a fresh system prompt each turn, so usage is recomputed every turn — which is also
/// why a "/compact" visibly drops it.
/// </para>
/// </summary>
public sealed record LlmUsage(int PromptTokens, int ResponseTokens, int ContextWindow)
{
    /// <summary>Tokens occupied by this turn = prompt + generated response.</summary>
    public int UsedTokens => PromptTokens + ResponseTokens;

    /// <summary>Tokens still free in the context window (never negative).</summary>
    public int RemainingTokens => Math.Max(0, ContextWindow - UsedTokens);
}
