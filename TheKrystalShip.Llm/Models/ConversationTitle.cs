namespace TheKrystalShip.Llm.Models;

/// <summary>
/// What a conversation is called — the ONE place a title is authored, for every surface.
/// <para>
/// A conversation's name is its first prompt, shortened. Until it holds a turn it has no prompt to be
/// named after, and it is called <see cref="NewConversation"/> — a name the store gives it, not an
/// absence a client has to invent a word for. A title is therefore never null, which is what lets two
/// surfaces reading the same conversation show the same name: the alternative is each one choosing its
/// own label for null, and they choose differently.
/// </para>
/// </summary>
public static class ConversationTitle
{
    /// <summary>
    /// What a conversation with no turn in it is called. Stated in <c>docs/wire-contract.md</c> because
    /// a client holds a conversation of its own for the moment between minting an id and the leaf
    /// authoring it, and that moment must not read differently from every moment after it.
    /// </summary>
    public const string NewConversation = "New chat";

    /// <summary>The longest a title is kept. Slack over the ~40 a surface's rail shows.</summary>
    public const int MaxLength = 80;

    /// <summary>
    /// The conversation's name: <paramref name="firstPrompt"/> shortened, or
    /// <see cref="NewConversation"/> when there is no first prompt to take it from.
    /// </summary>
    public static string For(string? firstPrompt) =>
        string.IsNullOrWhiteSpace(firstPrompt) ? NewConversation : Shorten(firstPrompt);

    /// <summary>
    /// One prompt on one line, capped with an ellipsis — how a prompt reads anywhere a whole one will
    /// not fit. Separate from <see cref="For"/> because a prompt shown beside something else (a
    /// feedback note names the turn it was left on) is being quoted, not naming a conversation, and
    /// has no business acquiring a conversation's placeholder.
    /// </summary>
    public static string Shorten(string prompt)
    {
        var oneLine = prompt.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxLength ? oneLine : oneLine[..MaxLength].TrimEnd() + "…";
    }
}
