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

    /// <summary>
    /// Shown in place of <see cref="Correction"/> when the turn re-prompts itself: the sentence above
    /// it is wrong the moment it is written, and the attempt that follows it needs to be readable as a
    /// second attempt rather than as more of the first.
    /// </summary>
    public const string RetryNotice =
        "\n\n*(Correction — nothing was staged there; I described an action I had not taken. Doing it "
        + "properly now.)*\n\n";

    /// <summary>
    /// The model-facing half of the same moment: what the loop feeds back so the next round acts
    /// instead of narrating. It states what is measured — no tool was called, so nothing is staged —
    /// then puts <paramref name="userPrompt"/> back in front of the model with both answers open.
    /// <para>
    /// Both halves are load-bearing, measured against <c>gemma4:12b</c> in a conversation already
    /// holding fabricated replies: without "the tool call itself and no prose" the model apologises and
    /// narrates the same fabrication again, and without the request restated it answers a "thanks,
    /// that's all" by staging the action from the reply it is being corrected about.
    /// </para>
    /// </summary>
    public static string NudgeFor(string userPrompt) =>
        "Your last reply described a staged or completed action, but you called no tool, so nothing was "
        + "staged and no confirmation exists. Saying it happened does not make it happen. Answer the "
        + $"request \"{Excerpt(userPrompt)}\" again: if it needs an action, reply with the tool call "
        + "itself and no prose; if it needs none, say so plainly and claim no action.";

    /// <summary>How much of the request the nudge restates. Enough to identify it, never a pasted file.</summary>
    private const int MaxRestatedPromptChars = 300;

    private static string Excerpt(string prompt)
    {
        var trimmed = (prompt ?? string.Empty).Trim();
        return trimmed.Length <= MaxRestatedPromptChars
            ? trimmed
            : trimmed[..MaxRestatedPromptChars] + "…";
    }

    /// <summary>
    /// Whether <paramref name="reply"/> already carries the correction — the guard's own text, which
    /// matches the claim pattern it describes and must never be appended twice.
    /// </summary>
    public static bool CorrectionIsPresentIn(string? reply) =>
        reply is not null && reply.Contains(Correction, StringComparison.Ordinal);

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
