namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A gate decision returned by the host for a single tool call, evaluated by the
/// agent loop immediately before dispatch. This is how an application enforces
/// its own policy (authorization, rate/blast-radius caps, confirmation) without
/// the library needing to know anything about what the tools mean.
/// </summary>
public sealed record ToolGate(bool Allowed, string? RefusalMessage)
{
    /// <summary>Allow the call to be dispatched.</summary>
    public static ToolGate Allow { get; } = new(true, null);

    /// <summary>
    /// Refuse the call. The <paramref name="message"/> is fed back to the model
    /// as the tool result (in place of execution) so it can adapt or explain.
    /// </summary>
    public static ToolGate Refuse(string message) => new(false, message);
}

/// <summary>
/// Everything the host supplies for one agent turn. The library owns the
/// model↔tool loop; the host owns the policy: which tools are offered this turn,
/// the system prompt, and the per-call <see cref="Gate"/>.
/// </summary>
public sealed record AgentTurn
{
    /// <summary>
    /// Opaque key identifying the conversation for memory purposes (e.g. a
    /// Discord "{userId}:{channelId}", or a web session id). History is stored
    /// and retrieved per conversation id.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>The user's message for this turn.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>
    /// The name to attribute <see cref="UserPrompt"/> to in the model's view, for a conversation
    /// several people share. Null leaves the prompt unattributed — the right shape for a
    /// conversation with one participant, where a label answers a question nobody asked.
    /// </summary>
    /// <remarks>
    /// Model-facing only: the recorded turn keeps <see cref="UserPrompt"/> verbatim, and the label is
    /// re-derived on replay from the display name stored against each turn. Storing the composed
    /// string instead would make the log disagree with what the person actually typed, and every
    /// later reader — the review surface, the compactor, a re-index — would inherit the disagreement.
    /// </remarks>
    public string? Speaker { get; init; }

    /// <summary>
    /// The system prompt, built fresh by the host each turn. Not persisted to
    /// conversation memory; it is prepended to the working message list only.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Optional fingerprint of the <i>tunable</i> part of the system prompt — the editable template,
    /// excluding any per-turn injected data (e.g. live inventory lists). When set, the recorder
    /// stamps it instead of hashing the whole assembled <see cref="SystemPrompt"/>, so the id moves
    /// only when the operator edits the prompt, not when inventory changes. Null ⇒ the agent loop
    /// falls back to hashing <see cref="SystemPrompt"/>.
    /// </summary>
    public string? SystemPromptHash { get; init; }

    /// <summary>
    /// Tools the model may call this turn. This set IS the whitelist — the host
    /// decides what to offer (e.g. read-only vs. full) per caller.
    /// </summary>
    public required IReadOnlyList<LlmToolDefinition> Tools { get; init; }

    /// <summary>
    /// Optional per-call gate evaluated before each dispatch. Return
    /// <see cref="ToolGate.Allow"/> to execute, or <see cref="ToolGate.Refuse"/>
    /// to feed a refusal back to the model without executing. A closure created
    /// per turn can hold mutable state (e.g. an action counter) to implement caps.
    /// When null, every requested tool call is dispatched.
    /// </summary>
    public Func<LlmToolCall, ToolGate>? Gate { get; init; }

    /// <summary>
    /// When true, enables the model's thinking/reasoning mode for this turn. The model
    /// generates an internal chain-of-thought before producing its final answer.
    /// Defaults to false (no thinking); the host resolves this from the client's request
    /// or falls back to the server-wide <c>Ollama:Think</c> config default.
    /// </summary>
    public bool Think { get; init; }

    /// <summary>
    /// Optional display name of the user this turn is for, recorded verbatim onto the turn
    /// (<see cref="ConversationTurnRecord.UserDisplay"/>). The library neither parses nor derives it —
    /// only the host knows who the opaque <see cref="ConversationId"/> belongs to. Leave null and the
    /// turn simply records no name.
    /// </summary>
    public string? UserDisplay { get; init; }
}
