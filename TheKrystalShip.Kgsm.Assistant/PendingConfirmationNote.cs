using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Detects a reply that leaves a staged action unmentioned, and supplies the sentence that names it.
/// <para>
/// The mirror of <see cref="UnbackedActionClaim"/>. That one catches a reply claiming an action the
/// turn never took; this one catches the opposite error, which misinforms just as badly: the turn
/// staged something real — a confirmation prompt is on the user's screen — and the reply says
/// nothing about it, or reports the turn as unfinished. A user told "I couldn't finish that" while
/// looking at a Confirm button has to guess what pressing it does, and the safest guess is wrong in
/// both directions.
/// </para>
/// <para>
/// Decided from the turn rather than the prose: <see cref="ServerAssistant"/> knows what was staged
/// and only asks this class when the answer is "something", so the match need only separate a reply
/// that already says a confirmation is pending from one that does not.
/// </para>
/// </summary>
public static partial class PendingConfirmationNote
{
    /// <summary>
    /// Appended to a reply that staged an action without saying so. Names the count rather than the
    /// operations: the confirmation prompts carry their own descriptions, and restating them here
    /// would be a second place for what an action is to be described, free to drift from the first.
    /// </summary>
    public static string For(int stagedCount) =>
        stagedCount == 1
            ? "\n\n**Awaiting your confirmation.** I've proposed one action — it has not run yet, and "
              + "only will if you confirm it."
            : $"\n\n**Awaiting your confirmation.** I've proposed {stagedCount} actions — none has run "
              + "yet, and each only will if you confirm it.";

    /// <summary>
    /// Whether the reply already tells the user something is waiting on them. Broad on purpose: the
    /// cost of missing a phrasing is one redundant sentence, while the cost of a false match is a
    /// confirmation prompt the reply never explains.
    /// </summary>
    public static bool IsPresentIn(string reply) => !string.IsNullOrWhiteSpace(reply) && Pending().IsMatch(reply);

    [GeneratedRegex(
        @"awaiting|await your|confirm|approve|approval|go-?ahead|" +
        @"(propos|stag)(e|ed|ing)|won'?t (run|happen|start) until|once you",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pending();
}
