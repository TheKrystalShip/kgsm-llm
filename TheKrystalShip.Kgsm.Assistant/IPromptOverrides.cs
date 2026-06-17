using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The hot-editable prompt layer: reads operator-supplied prompt text and tool-description overrides
/// from a configured directory, re-read on every turn so an edit applies to the NEXT turn with no
/// restart. Failure-isolated — an absent/blank/unreadable override falls back to the in-code default,
/// never blanks a prompt or breaks a turn. This is the seam that lets prompts and tool descriptions
/// be tuned against the recorded transcript corpus without recompiling.
/// </summary>
public interface IPromptOverrides
{
    /// <summary>
    /// Returns the override text for a prompt-segment file (e.g. <c>preamble.md</c>) in the prompts
    /// directory, trimmed; <c>null</c> if there is no directory, the file is absent/blank, or it
    /// can't be read (so the caller falls back to config/constant).
    /// </summary>
    string? ReadText(string fileName);

    /// <summary>
    /// Returns a copy of <paramref name="tools"/> with descriptions and parameter descriptions
    /// overridden from <c>tools.json</c> where present (tool NAMES are structural and never changed);
    /// returns <paramref name="tools"/> unchanged when there is nothing to apply or the file is bad.
    /// </summary>
    IReadOnlyList<LlmToolDefinition> OverlayTools(IReadOnlyList<LlmToolDefinition> tools);
}
