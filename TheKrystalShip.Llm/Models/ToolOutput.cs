namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The two outputs of a single tool execution: the model-facing <see cref="Summary"/> text (fed
/// back into the conversation as the tool result the model reasons over) and an optional,
/// surface-facing <see cref="Data"/> payload the agent loop carries <em>opaquely</em> to a
/// streaming consumer — never inspected here, and never shown to the model.
/// <para>
/// <see cref="Data"/> is deliberately <see cref="object"/>: the generic agent loop stays
/// domain-agnostic, so a host can attach any structured card (e.g. the KGSM assistant's
/// <c>ToolResult&lt;TData&gt;</c> card) without this package taking a domain dependency. The loop
/// only ever moves it from the dispatcher to the <see cref="AgentEventKind.ToolResult"/> event; it
/// is the consumer (a surface) that interprets it. A tool that has no card returns just the
/// summary — the implicit <see cref="string"/> conversion keeps those call sites unchanged.
/// </para>
/// </summary>
/// <param name="Summary">The model-grounding text — the conversational tool result.</param>
/// <param name="Data">An optional structured payload for a surface; opaque to the loop, hidden from the model.</param>
public sealed record ToolOutput(string Summary, object? Data = null)
{
    /// <summary>
    /// A summary-only output. Lets a dispatcher keep returning a bare <c>string</c> (e.g.
    /// <c>return "Error: …";</c>) unchanged while the loop's contract widens to carry an optional card.
    /// </summary>
    public static implicit operator ToolOutput(string summary) => new(summary);
}
