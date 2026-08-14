namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The host's verdict on a reply the model has just produced, evaluated by the agent loop before
/// the turn is recorded and handed over. The counterpart of <see cref="ToolGate"/> on the way out:
/// the library owns the loop, the host owns the policy — only the host knows what the turn was
/// supposed to do and what it actually did.
/// </summary>
/// <param name="Nudge">
/// Model-facing instruction that runs the turn for another round with this reply and the nudge
/// appended to the working list. The loop honours one retry per turn; a second request to retry is
/// treated as an amendment, so a host cannot spin the loop on a model that keeps making the same
/// reply.
/// </param>
/// <param name="Amendment">
/// User-facing text appended to the reply — shown, recorded, and part of the turn's final text. The
/// reply is already on a screen by the time it is reviewed, so a verdict that contradicts it can
/// only add to it, never take it back.
/// </param>
public sealed record ReplyReview(string? Nudge = null, string? Amendment = null)
{
    /// <summary>The reply stands as written.</summary>
    public static ReplyReview Accept { get; } = new();

    /// <summary>Run another round with <paramref name="nudge"/>, optionally qualifying what was already shown.</summary>
    public static ReplyReview Retry(string nudge, string? amendment = null) => new(nudge, amendment);

    /// <summary>Keep the reply, with <paramref name="amendment"/> appended to it.</summary>
    public static ReplyReview Amend(string amendment) => new(null, amendment);

    public bool WantsRetry => !string.IsNullOrEmpty(Nudge);

    public bool HasAmendment => !string.IsNullOrEmpty(Amendment);
}
