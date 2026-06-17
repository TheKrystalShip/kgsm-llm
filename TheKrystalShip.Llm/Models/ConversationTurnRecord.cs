namespace TheKrystalShip.Llm.Models;

/// <summary>
/// How a recorded turn ended. <see cref="Ok"/> reached a final reply; <see cref="Error"/> failed in
/// the backend; <see cref="CapHit"/> exhausted the iteration cap without a final answer;
/// <see cref="Cancelled"/> was abandoned mid-turn (e.g. Ctrl-C / client disconnect).
/// </summary>
public enum TurnOutcome
{
    Ok,
    Error,
    CapHit,
    Cancelled
}

/// <summary>
/// One tool invocation within a turn, captured for offline analysis: the tool the model picked,
/// the arguments it passed, the <b>raw, pre-truncation</b> result it got back, and how long the
/// call took. A refused call carries the refusal string as its <see cref="Result"/> with a zero
/// duration (it never reached the dispatcher).
/// </summary>
public sealed record RecordedToolCall(
    string Name,
    IReadOnlyDictionary<string, string?> Arguments,
    string Result,
    long DurationMs);

/// <summary>
/// An append-only, per-turn record of one model↔tool turn, captured at the agent loop (the one
/// place the whole picture coexists) for the purpose of self-improvement analysis — NOT for feeding
/// the model. It is the per-turn <i>delta</i>: the user prompt, this turn's tool trajectory, and the
/// final reply, plus the metadata needed to bucket and compare turns over time (system-prompt hash,
/// iteration count, token usage, outcome). Reconstruct a whole conversation by filtering on
/// <see cref="ConversationId"/> and ordering by <see cref="CompletedAt"/>.
/// </summary>
public sealed record ConversationTurnRecord
{
    /// <summary>The opaque, surface-prefixed conversation id (e.g. <c>cli:…</c>, <c>web:…</c>).</summary>
    public required string ConversationId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>The user's prompt, verbatim.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>
    /// A short, stable hash of the system prompt that drove this turn. Lets analysis bucket turns
    /// "before vs after" a prompt change without storing the (large, repeated) prompt every line.
    /// </summary>
    public required string SystemPromptHash { get; init; }

    /// <summary>The ordered tool trajectory for this turn (empty for a pure-prose turn).</summary>
    public required IReadOnlyList<RecordedToolCall> Tools { get; init; }

    /// <summary>Number of model↔tool round-trips the loop ran for this turn.</summary>
    public required int Iterations { get; init; }

    public required TurnOutcome Outcome { get; init; }

    /// <summary>The final assistant reply; <c>null</c> for an error/cancelled turn with no reply.</summary>
    public string? Final { get; init; }

    /// <summary>Per-turn context occupancy (prompt/response/window tokens), when the backend reported it.</summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>The failure message for an <see cref="TurnOutcome.Error"/> turn.</summary>
    public string? Error { get; init; }
}
