namespace TheKrystalShip.Kgsm.Assistant.Envelope;

/// <summary>
/// The surface-facing projection of a <see cref="ToolResult{TData}"/> — the
/// payload of the <c>tool.result.result</c> card. Non-generic on purpose: it rides the agent
/// loop's opaque <c>object?</c> data channel from the dispatcher to the SSE boundary without the
/// generic <c>TheKrystalShip.Llm</c> package ever knowing the card type (<see cref="Data"/> is
/// boxed and serialised by its runtime type at the Service edge).
/// <para>
/// The model-facing <see cref="ToolResult{TData}.Summary"/> is deliberately OMITTED here: it
/// already travels on the <c>tool.result</c> frame's own <c>summary</c> field. Two readers, one
/// result — the model gets the grounding text, the surface gets this card — so duplicating the
/// summary inside the card would be redundant.
/// </para>
/// </summary>
/// <param name="Tool">The producing tool's name — also the surface's card kind (what it switches rendering on).</param>
/// <param name="Confidence">Trust in the result's conclusion.</param>
/// <param name="Subject">What the result is about.</param>
/// <param name="Data">The structured card payload (the envelope's <c>TData</c>, boxed).</param>
/// <param name="Links">Optional related references for the surface to route to.</param>
public sealed record ToolResultCard(
    string Tool,
    Confidence Confidence,
    ResultRef Subject,
    object Data,
    IReadOnlyList<ResultRef>? Links = null)
{
    /// <summary>Projects a typed <see cref="ToolResult{TData}"/> into the transport card (its Summary dropped).</summary>
    public static ToolResultCard From<TData>(ToolResult<TData> result) =>
        new(result.Tool.Name, result.Confidence, result.Subject, result.Data!, result.Links);
}
