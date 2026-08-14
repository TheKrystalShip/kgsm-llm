namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The shape a caller needs this turn's reply in. It is a property of the SURFACE the answer lands
/// on, not of the person or the question, so it travels per turn rather than per leaf: one leaf can
/// carry both — kgsm-bot answers a typed message in a text channel and a spoken one in a voice
/// channel, and the same account asks both.
/// </summary>
/// <remarks>
/// This selects presentation only. What the model may do, which tools it is offered, how a mutation
/// is staged and how a staged one must be reported are unchanged by it — a style can make an answer
/// shorter, never less true and never less complete about a pending confirmation.
/// </remarks>
public enum ReplyStyle
{
    /// <summary>Prose read on a screen: the assistant's full answer, as rich as the question warrants.</summary>
    Default,

    /// <summary>
    /// Prose that will be read aloud by a speech synthesiser. Speech cannot be skimmed, so every word
    /// costs the listener seconds they cannot skip; the reply is the answer and nothing around it.
    /// </summary>
    Voice,
}

/// <summary>Reads the wire form of a <see cref="ReplyStyle"/>.</summary>
public static class ReplyStyles
{
    /// <summary>The wire name a caller sends for <see cref="ReplyStyle.Voice"/>.</summary>
    public const string VoiceWire = "voice";

    /// <summary>The wire name a caller sends for <see cref="ReplyStyle.Default"/>.</summary>
    public const string DefaultWire = "default";

    /// <summary>
    /// Parses what a caller sent. Anything unrecognised — absent, blank, a typo, a style a newer
    /// caller knows about and this build does not — is <see cref="ReplyStyle.Default"/>: a surface
    /// that fails to make itself understood gets the full answer, which is readable everywhere.
    /// Answering a misspelt style with a 400 would break a caller over presentation.
    /// </summary>
    public static ReplyStyle Parse(string? wire) =>
        string.Equals(wire?.Trim(), VoiceWire, StringComparison.OrdinalIgnoreCase)
            ? ReplyStyle.Voice
            : ReplyStyle.Default;

    /// <summary>The wire name for a style, for a caller writing one.</summary>
    public static string ToWire(this ReplyStyle style) =>
        style == ReplyStyle.Voice ? VoiceWire : DefaultWire;
}
