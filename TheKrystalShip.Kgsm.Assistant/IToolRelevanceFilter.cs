using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Context a <see cref="IToolRelevanceFilter"/> may use to narrow the tool set for
/// a single turn. Authority has already been applied upstream (the filter only ever
/// sees the tools the caller is permitted to use), so this carries request shape,
/// not permissions.
/// </summary>
/// <param name="UserPrompt">The user's message for this turn.</param>
/// <param name="CanPerformActions">Whether the caller is authorized for actions (already reflected in the authorized set).</param>
public sealed record ToolSelectionContext(string UserPrompt, bool CanPerformActions);

/// <summary>
/// The second, orthogonal tool-subsetting axis: after
/// <em>authorization</em> picks the tools a caller MAY use, <em>relevance</em> may
/// narrow them to those pertinent to the request before the prompt reaches the
/// (small, local) model — small-model selection accuracy, not context size, is the
/// binding constraint.
/// <para>
/// This is the SEAM, not a filter. The shipped default (<see cref="NoopToolRelevanceFilter"/>)
/// is a deliberate no-op: the current answer to small-model reliability is coarse,
/// bulk tools (e.g. the merged fleet <c>server_info</c>), not relevance filtering —
/// keyword routing fails silently on unanticipated phrasing and a second embedding
/// model would fight game servers for VRAM. The seam exists so a filter can be
/// slotted in later WITHOUT the rest of the pipeline having assumed "all authorized
/// tools are always present." Revisit only if a coarse catalog still overwhelms
/// selection under test.
/// </para>
/// </summary>
public interface IToolRelevanceFilter
{
    /// <summary>
    /// Returns the subset of <paramref name="authorized"/> to offer the model this
    /// turn. The no-op default returns <paramref name="authorized"/> unchanged.
    /// </summary>
    IReadOnlyList<LlmToolDefinition> GetToolsFor(
        ToolSelectionContext context,
        IReadOnlyList<LlmToolDefinition> authorized);
}

/// <summary>
/// The deliberate no-op relevance filter (see <see cref="IToolRelevanceFilter"/>):
/// returns the authorized tool set unchanged. This is the DECISION (coarse/bulk
/// tools are the fix), not a placeholder.
/// </summary>
public sealed class NoopToolRelevanceFilter : IToolRelevanceFilter
{
    /// <inheritdoc/>
    public IReadOnlyList<LlmToolDefinition> GetToolsFor(
        ToolSelectionContext context,
        IReadOnlyList<LlmToolDefinition> authorized) => authorized;
}
