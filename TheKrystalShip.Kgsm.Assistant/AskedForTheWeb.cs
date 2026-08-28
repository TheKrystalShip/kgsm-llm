namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Whether the person asking said, in so many words, to look it up online.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because telling the model was not enough.</b> The <c>search</c> tool takes a
/// scope, and its description says to pass <c>web</c> when the user asks to check online. Measured on
/// this host with <c>gemma4:12b</c>: asked in plain English to check online, the model called
/// <c>search</c> with the query alone and no scope at all, every time — so the default ladder ran,
/// local documentation matched the game on topic, and the web was never reached. An optional
/// parameter is a suggestion; this is not.
/// </para>
/// <para>
/// <b>It reads an instruction, never a topic.</b> Nothing here guesses whether a question needs
/// current information — that judgement is the model's and stays there. This matches only phrases
/// that are a person telling the assistant where to look, which is a fact about what they typed
/// rather than an inference about what they meant.
/// </para>
/// <para>
/// <b>It can only widen.</b> A match sends the search to the web; no phrase here ever confines one to
/// the documentation, and no phrase disables a search. The worst a false positive can do is answer
/// from the web something the local docs also covered.
/// </para>
/// </remarks>
public static class AskedForTheWeb
{
    /// <summary>
    /// Ways of saying "look it up out there".
    /// </summary>
    /// <remarks>
    /// Deliberately all two-word-or-longer instructions. A bare "online" or "web" appears in ordinary
    /// questions about servers being online, which is the opposite subject.
    /// </remarks>
    private static readonly string[] Phrases =
    [
        "search online", "search the web", "search the internet", "search on the web",
        "search on the internet", "search for it online", "web search",
        "look online", "look it up online", "look that up online", "look this up online",
        "look on the web", "look on the internet", "look it up on the web",
        "look it up on the internet",
        "check online", "check the web", "check the internet", "check on the web",
        "check on the internet", "check it online",
        "find online", "find it online", "find out online",
        "browse the web", "browse the internet", "on the internet", "from the internet",
        "google it", "google that", "google for",
        "from the web", "off the web", "up online", "it online", "that online",
    ];

    /// <summary>Whether <paramref name="prompt"/> asks for the web outright.</summary>
    public static bool In(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        // Punctuation folded to spaces so "check online?" and "online, please" match the same as the
        // bare phrase, and padded so a match is always on whole words.
        var text = new string(prompt.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        var padded = $" {string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries))} ";

        return Phrases.Any(phrase => padded.Contains($" {phrase} ", StringComparison.Ordinal));
    }
}
