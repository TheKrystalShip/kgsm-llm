namespace TheKrystalShip.Llm.Agent;

/// <summary>
/// Tuning knobs for the agent loop. These are generic loop-safety concerns, not
/// application policy (caps/authorization live in the host's per-turn gate).
/// </summary>
public class LlmAgentOptions
{
    public const string Section = "LlmAgent";

    /// <summary>
    /// Safety cap on model↔tool round-trips within a single user turn. It exists to stop a runaway
    /// loop, not to ration work: a turn that is still calling tools is still working, and cutting it
    /// off mid-task spends everything already done and returns nothing. Set it well above what a
    /// direct trajectory costs, so only a genuine loop reaches it.
    /// </summary>
    public int MaxIterations { get; set; } = 25;

    /// <summary>
    /// Tool outputs are truncated to this many characters before being fed back to
    /// the model, to protect the context window.
    /// </summary>
    public int MaxToolOutputChars { get; set; } = 1500;

    /// <summary>
    /// Reply returned when the loop hits <see cref="MaxIterations"/> without the
    /// model producing a final text answer.
    /// </summary>
    public string IterationLimitReply { get; set; } =
        "I wasn't able to finish that after a few steps — could you rephrase or break it down?";

    /// <summary>
    /// Reply returned when the model finishes a turn having produced no answer at all. A backend can
    /// end a generation with empty content — it exhausts the context mid-sentence, or spends the
    /// whole budget reasoning and never writes a reply — and an empty string is not something a
    /// surface can deliver: it reaches a person as silence, indistinguishable from the assistant
    /// ignoring them. Saying so is what makes the failure visible.
    /// </summary>
    public string EmptyReplyReply { get; set; } =
        "I got tangled up and didn't manage to answer that — could you ask me again?";
}
