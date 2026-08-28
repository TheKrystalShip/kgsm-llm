namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Somebody asked for the web and the turn answered without looking.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third and last way this failed, each one a step further than the fix before it.</b>
/// First the model called <c>search</c> and local documentation shadowed the web. Then it called
/// <c>search</c> without the scope that would have reached the web. Then — measured on this host —
/// it answered <em>"look it up online"</em> with no tool call whatsoever, out of its own parametric
/// knowledge, so there was no scope to force and nothing to route.
/// </para>
/// <para>
/// <b>Answering from memory something you were asked to look up is a fabricated status</b> — the
/// ecosystem rule, applied to a fact about the world rather than about a server. The reply reads as
/// researched and is not, and nothing in it says so.
/// </para>
/// <para>
/// <b>The correction is the same shape as <see cref="UnbackedActionClaim"/></b>: re-prompt once with
/// what is measured, and if the second attempt still does not look, leave the reply standing with an
/// honest note rather than discarding content that may be useful. One re-prompt, because a model that
/// will not call a tool twice will not call it a third time either, and the person is waiting.
/// </para>
/// </remarks>
public static class UnsearchedWebRequest
{
    /// <summary>Appended when the turn was asked to look online and never did.</summary>
    public const string Correction =
        "\n\n**Note — I did not actually look this up.** You asked me to check online and I answered "
        + "from what I already had, which may be out of date or wrong. Ask me again if you want it "
        + "searched properly.";

    /// <summary>Shown in place of <see cref="Correction"/> when the turn re-prompts itself.</summary>
    public const string RetryNotice =
        "\n\n*(That answer wasn't looked up — checking online now.)*\n\n";

    /// <summary>
    /// The model-facing half: what the loop feeds back so the next round searches instead of
    /// recalling.
    /// </summary>
    /// <remarks>
    /// It restates the request because the instruction is routinely a follow-up with no subject in it
    /// — <em>"look it up online"</em> on its own means the thing just discussed, and a nudge that
    /// does not say so invites a search for the words "look it up online".
    /// </remarks>
    public static string NudgeFor(string userPrompt) =>
        "You were asked to look this up online and you answered without calling any tool, so nothing "
        + "was actually looked up. Call the search tool now with scope=\"web\". If the request "
        + $"(\"{Excerpt(userPrompt)}\") does not name the subject itself, take it from what this "
        + "conversation is about. Reply with the tool call and no prose.";

    /// <summary>How much of the request the nudge restates. Enough to identify it, never a pasted file.</summary>
    private static string Excerpt(string userPrompt)
    {
        var text = userPrompt.Trim().ReplaceLineEndings(" ");
        return text.Length <= 160 ? text : text[..159] + "…";
    }
}
