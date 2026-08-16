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
    /// <remarks>
    /// <paramref name="leaf"/> is the leaf this turn is being run for (<c>kgsm-bot</c>,
    /// <c>kgsm-api</c>). Its own file in <c>&lt;directory&gt;/&lt;leaf&gt;/</c> wins when present;
    /// otherwise the host-wide file at the root answers, which is the assistant's own text.
    /// <see langword="null"/> — the CLI, the assistant's own web client, any caller that names no
    /// leaf — reads the root directly.
    /// </remarks>
    string? ReadText(string fileName, string? leaf = null);
}
