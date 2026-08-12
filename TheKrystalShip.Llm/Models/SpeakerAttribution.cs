namespace TheKrystalShip.Llm.Models;

/// <summary>
/// How a shared conversation names who is speaking, in the one form both the live turn and the
/// replayed history use.
/// </summary>
/// <remarks>
/// <para>
/// A conversation with one participant needs none of this: "you" is unambiguous, and a label would
/// answer a question nobody asked. A conversation several people share needs it for every line, or
/// the model reads a transcript in which everyone is the same person — and answers "as you said
/// earlier" to somebody who said nothing of the kind.
/// </para>
/// <para>
/// One type because the live prompt and the replayed history must be attributed <em>identically</em>.
/// Formatted two ways, the newest message reads as a different kind of thing from the messages above
/// it, which is exactly the seam a model resolves by guessing.
/// </para>
/// </remarks>
public static class SpeakerAttribution
{
    /// <summary>
    /// Upper bound on a rendered speaker label. Display names are user-controlled and arrive from a
    /// chat service with far looser limits than anything the label is worth spending context on.
    /// </summary>
    public const int MaxSpeakerLength = 64;

    /// <summary>
    /// <paramref name="prompt"/> as the model sees it: attributed to <paramref name="speaker"/> when
    /// there is one to name, and verbatim when there is not.
    /// </summary>
    public static LlmMessage Message(string? speaker, string prompt) =>
        LlmMessage.User(Compose(speaker, prompt));

    /// <summary>
    /// The attributed text itself — <c>"{speaker}: {prompt}"</c>, or <paramref name="prompt"/>
    /// unchanged when <paramref name="speaker"/> names nobody.
    /// </summary>
    public static string Compose(string? speaker, string prompt)
    {
        var label = Label(speaker);
        return label is null ? prompt : $"{label}: {prompt}";
    }

    /// <summary>
    /// A display name reduced to something safe to put in front of a prompt, or <see langword="null"/>
    /// when nothing usable is left.
    /// </summary>
    /// <remarks>
    /// Line breaks and colons are removed rather than escaped. A display name is chosen by the person
    /// it belongs to, and one containing either could otherwise typeset itself as a second speaker's
    /// line — putting words in someone else's mouth in a transcript the model reads as fact. Removing
    /// the two characters that draw that boundary costs a faithful rendering of an unusual name and
    /// buys a transcript whose speaker labels only the host can write.
    /// </remarks>
    public static string? Label(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return null;

        var cleaned = new string(speaker
            .Where(c => c != ':' && !char.IsControl(c))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
            return null;

        return cleaned.Length <= MaxSpeakerLength ? cleaned : cleaned[..MaxSpeakerLength].TrimEnd();
    }
}
