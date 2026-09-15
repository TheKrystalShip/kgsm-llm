using TheKrystalShip.Agent.Replies;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The claim-of-acting check in this assistant's words: the verbs a game server is acted on with, and
/// the correction that says no server was touched.
/// </summary>
/// <remarks>
/// The verbs are drawn from replies <c>gemma4:12b</c> actually produced on turns that staged and ran
/// nothing — a staged backup, a halted process, an updated draft — and the offers that must stay
/// honest beside them. <c>UnbackedActionClaimTests</c> holds those replies verbatim.
/// </remarks>
public static class ServerActionClaim
{
    public static readonly UnbackedActionClaim Check = new(new ActionClaimWords
    {
        CompletedVerbs =
            @"staged|queued|halted|stopped|started|restarted|rebooted|backed[ -]?up|updated|installed|"
            + @"uninstalled|deleted|removed|reconfigured|shut (?:it |them |that )?down|"
            + @"set (?:it |them |that |this )?up|"
            + @"turned (?:it |them |that )?(?:on|off)|kicked off|opened (?:the )?ports?|forwarded",
        OfferedVerbs = "stage|start|stop|restart|back|update|install|open",
        Correction =
            "\n\n**Correction — nothing was actually staged or changed.** I reported an action I did not "
            + "take: no confirmation is pending and no server was touched. Please ask me again.",
        RetryNotice =
            "\n\n*(Correction — nothing was staged there; I described an action I had not taken. Doing it "
            + "properly now.)*\n\n",
    });
}
