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
/// One tool invocation within a turn. The fields mirror the assistant's §5·a <c>tool.start</c> +
/// <c>tool.result</c> wire events so a surface can re-scaffold the call visually from history without a
/// second schema: the tool the model picked (<see cref="Name"/>), the <see cref="Arguments"/> it passed,
/// the model-facing <see cref="Summary"/> string (the <b>raw, pre-truncation</b> output), and the
/// optional structured <see cref="Card"/> (the §5·a <c>tool.result.result</c> card — present only for
/// tools that emit one, e.g. <c>run_health_check</c>; <c>null</c> otherwise). <see cref="DurationMs"/> is
/// analysis-only (the §5·a projection omits it). A refused call carries the refusal string as its
/// <see cref="Summary"/> with a zero duration (it never reached the dispatcher).
/// </summary>
public sealed record RecordedToolCall(
    Tool Name,
    IReadOnlyDictionary<string, string?> Arguments,
    string Summary,
    long DurationMs,
    object? Card = null);

/// <summary>
/// The canonical, append-only record of one completed model↔tool turn — the per-turn <i>delta</i>
/// (this turn's content only, never a cumulative snapshot): the user prompt, the model's thinking,
/// this turn's tool trajectory, and the final reply, plus the metadata needed to replay and to
/// compare turns over time (system-prompt hash, iteration count, token usage, outcome). It is BOTH
/// the conversation history (a <see cref="ConversationEntry"/> of kind <see cref="ConversationEntryKind.Turn"/>
/// in the store) AND the self-improvement record — one canon log, captured at the agent loop (the one
/// place the whole picture coexists). Reconstruct a whole conversation by replaying entries for a
/// <see cref="ConversationId"/> in order; the model only ever replays <see cref="UserPrompt"/> +
/// <see cref="Final"/> (the rich fields are for display + analysis, never fed back).
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

    /// <summary>Whether the "think" mode was enabled for this turn (the per-turn reasoning toggle).</summary>
    public required bool Think { get; init; }

    /// <summary>
    /// The model's internal reasoning text for this turn, when <see cref="Think"/> was on and the
    /// backend streamed it (the streaming path). <c>null</c> when thinking was off or unavailable
    /// (e.g. the buffered path). Captured for analysis; never replayed into the model's context.
    /// </summary>
    public string? Thinking { get; init; }

    /// <summary>The final assistant reply; <c>null</c> for an error/cancelled turn with no reply.</summary>
    public string? Final { get; init; }

    /// <summary>Per-turn context occupancy (prompt/response/window tokens), when the backend reported it.</summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>The failure message for an <see cref="TurnOutcome.Error"/> turn.</summary>
    public string? Error { get; init; }
}
