namespace TheKrystalShip.Llm.Models;

/// <summary>How a person judged one recorded turn.</summary>
public enum TurnFeedbackRating
{
    /// <summary>The answer helped.</summary>
    Up,

    /// <summary>The answer did not help.</summary>
    Down
}

/// <summary>
/// One person's verdict on one recorded turn — the only signal in the corpus that says whether an
/// answer was actually any <i>good</i>. Everything else the log captures (outcome, latency, trajectory)
/// describes how a turn ran, not whether it helped.
/// <para>
/// It is written only by the person whose conversation it is, so it is a satisfaction signal and never
/// a reviewer's judgement. <see cref="Note"/> is the free-text "what went wrong", offered on a
/// <see cref="TurnFeedbackRating.Down"/> and optional even then — a rating says a turn was bad, only the
/// note says why. It is never replayed into the model's context: like <see cref="ConversationTurnRecord.Thinking"/>
/// this is an analysis record, and feeding a verdict back as conversation would change the next answer.
/// </para>
/// </summary>
public sealed record TurnFeedback(
    TurnFeedbackRating Rating,
    string? Note,
    DateTimeOffset At);
