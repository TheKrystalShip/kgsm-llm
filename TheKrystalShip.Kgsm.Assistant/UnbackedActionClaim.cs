using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Detects a reply that asserts the assistant staged or performed a server action on a turn that
/// staged and performed nothing.
/// <para>
/// The model narrates its own turn, and it is occasionally wrong about it: it answers a mutating
/// request conversationally — no tool call at all, or only a read — and then reports the action as
/// staged or done. A reply saying "I've staged a backup" when nothing was staged is a fabricated
/// status, which is the one thing this ecosystem never ships; the user is told to expect a
/// confirmation prompt that does not exist, or that a server was stopped when it is still running.
/// </para>
/// <para>
/// This is decided from the turn, not from the prose alone: <see cref="ServerAssistant"/> knows
/// whether anything was staged or executed, and only asks this class when the answer is "nothing".
/// On such a turn any first-person claim of a completed or staged action is provably false, so the
/// match need only separate an assertion ("I've stopped it") from an offer ("I can stop it") or a
/// report of the world ("it was restarted an hour ago" — from the audit log, and true).
/// </para>
/// </summary>
public static partial class UnbackedActionClaim
{
    /// <summary>
    /// Appended to a reply whose action claim nothing backs. It contradicts the sentence before it,
    /// deliberately: the rest of the reply may carry real content (researched facts, a status read),
    /// so it is corrected rather than discarded.
    /// </summary>
    public const string Correction =
        "\n\n**Correction — nothing was actually staged or changed.** I reported an action I did not "
        + "take: no confirmation is pending and no server was touched. Please ask me again.";

    // Verbs that assert a completed or staged action, in the first person. Present/future forms are
    // absent on purpose ("I can stage", "I'll stop") — those promise, and promising is honest.
    private const string Verbs =
        @"staged|queued|halted|stopped|started|restarted|rebooted|backed[ -]?up|updated|installed|"
        + @"uninstalled|deleted|removed|reconfigured|shut (?:it |them |that )?down|"
        + @"set (?:it |them |that |this )?up|"
        + @"turned (?:it |them |that )?(?:on|off)|kicked off|opened (?:the )?ports?|forwarded";

    // The state a thing is asserted to be IN, as opposed to the act of putting it there. Reached by
    // the "I've got X staged" and "it is staged" constructions below.
    private const string StagedState = @"staged|queued|lined up|set up|ready to go";

    [GeneratedRegex(
        // "I've staged", "I have stopped", "I just backed up", "I successfully removed" …
        @"\bI(?:'ve|\s+have|\s+just)?\s+(?:successfully\s+|already\s+|now\s+)?(?:" + Verbs + @")\b"
        // "I've got the backup for Ketchup staged and ready to go" — the claim is the state the
        // object is asserted to be in, with the verb displaced past it.
        + @"|\bI(?:'ve|\s+have)\s+got\b[^.!?]{0,80}?\b(?:" + StagedState + @")\b"
        // "it's staged", "the backup has been queued" — asserted of the thing rather than the actor,
        // but on a turn that staged nothing it is the same false claim.
        + @"|\b(?:is|are|it's|that's|has been|have been)\s+(?:now\s+)?(?:" + StagedState + @")\b"
        // …or a reference to a confirmation prompt that was never posted.
        + @"|\bawaiting (?:your |the user's )?confirmation\b"
        + @"|\b(?:hit|click|press) confirm\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClaimPattern();

    // An offer is not a claim. Stripped before matching so "I can stage that for you, just say the
    // word" cannot trip the assertion pattern through the word it shares with it.
    [GeneratedRegex(
        @"\bI(?:'ll| will| can| could| would)\b[^.!?]{0,60}\b(?:stage|start|stop|restart|back|update|install|open)\b"
        + @"|\bwant me to\b|\bshould I\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex OfferPattern();

    /// <summary>
    /// Whether <paramref name="reply"/> claims an action the turn did not take. Ask this only for a
    /// turn that staged nothing and performed nothing — on any other turn the claim may well be true.
    /// </summary>
    public static bool IsPresentIn(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        return ClaimPattern().IsMatch(reply)
            && ClaimPattern().IsMatch(OfferPattern().Replace(reply, string.Empty));
    }
}
