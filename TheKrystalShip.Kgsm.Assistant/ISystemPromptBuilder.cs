namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Builds the per-turn system prompt: a configured persona/preamble shaped by the
/// caller's authorization, with the live instance + blueprint lists injected so the
/// model can answer and route directly without a grounding round-trip.
/// </summary>
public interface ISystemPromptBuilder
{
    /// <param name="canPerformActions">
    /// Whether the requesting user is authorized to run mutating actions. Shapes
    /// what the model is told it can do.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the lookup.</param>
    Task<string> BuildAsync(bool canPerformActions, CancellationToken cancellationToken = default);
}
