namespace TheKrystalShip.Kgsm.Assistant.Relay;

/// <summary>
/// The leaves that relay turns to the assistant, named once so a caller identifies itself with a
/// symbol rather than a typed string.
/// </summary>
/// <remarks>
/// The assistant validates the name it receives and falls back to its own prompts and origin for
/// anything it does not recognise — which is the right behaviour for an unknown leaf, and exactly
/// the wrong behaviour to debug after a typo. Referencing a constant removes that failure entirely,
/// and it is why this package carries no second copy of the validation rule: the receiver is the
/// boundary, and the sender simply cannot spell it wrong.
/// </remarks>
public static class RelayLeaf
{
    /// <summary>The Discord bot.</summary>
    public const string Bot = "kgsm-bot";

    /// <summary>The Control Panel API, which relays the browser chat.</summary>
    public const string Api = "kgsm-api";
}

/// <summary>
/// Writes the per-turn parts of a relayed turn: which leaf is calling, and the conversation the turn
/// belongs to.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for every leaf that relays a turn, because these headers decide which prompts a
/// turn reads, which audit origin it is recorded under, and which conversation it continues — and a
/// second hand-rolled copy is how two surfaces come to disagree about that.
/// </para>
/// <para>
/// <b>Who the turn is for, and what they may do, are not here.</b> A caller names the person on
/// <c>X-Kgsm-Acting</c> and authenticates as a member of the cluster with its own service token
/// (<c>ClusterCall</c>); the assistant resolves that person and their tier from its own replica of the
/// cluster's accounts. So a caller states who and never what, and nothing it sends can raise what
/// somebody may do.
/// </para>
/// <para>
/// The assistant parses each of these fail-closed. An auto-accept that is not exactly <c>true</c> is
/// propose-only, and an unrecognised leaf reads the assistant's own prompts under its own origin — so a
/// header this writer omits can never grant anything by omission.
/// </para>
/// </remarks>
public sealed class AssistantRelay
{
    /// <summary>The person's own auto-accept preference for this turn. A preference, never a permission:
    /// the assistant ANDs it with the tier it resolved for them.</summary>
    public const string AutoActHeader = "X-Relay-Auto-Act";

    /// <summary>A sub-scope of this user's own memory; never an identity.</summary>
    public const string ConversationIdHeader = "X-Relay-Conversation-Id";

    /// <summary>
    /// A conversation held in common by everyone in one place, rather than inside one person's
    /// memory — the exact opposite of <see cref="ConversationIdHeader"/>, and the reason it is a
    /// second header rather than a value of the first.
    /// </summary>
    /// <remarks>
    /// The assistant honours it only from a leaf it lists as permitted to open rooms, and never on
    /// its session-bearer path: a browser caller that could name a room could read a Discord thread's
    /// transcript. A leaf that does not send it is unaffected, and one that sends it without being
    /// permitted is answered as though it had not.
    /// </remarks>
    public const string RoomHeader = "X-Relay-Room";

    /// <summary>The leaf making the call, selecting its prompts and its audit origin.</summary>
    public const string LeafHeader = "X-Relay-Leaf";

    private readonly string _leaf;

    /// <param name="leaf">Which leaf this is; use a <see cref="RelayLeaf"/> constant.</param>
    public AssistantRelay(string leaf) => _leaf = leaf ?? string.Empty;

    /// <summary>
    /// Writes this leaf's name and the per-call parts onto <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// Authentication and the person acted for are written separately, by the transport's own
    /// <c>ClusterCall</c>: what proves the caller and who the call is for are the cluster's business,
    /// while what a turn reads and where it lands are the assistant's.
    /// </remarks>
    public void Write(HttpRequestMessage request, RelayCall? call = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_leaf.Length > 0)
            request.Headers.TryAddWithoutValidation(LeafHeader, _leaf);

        if (call is null)
            return;

        request.Headers.TryAddWithoutValidation(AutoActHeader, call.AutoAct ? "true" : "false");

        // The assistant sanitises this to [A-Za-z0-9_-] and treats what is left of a blank value as no
        // sub-scope at all, so there is nothing for a whitespace-only header to say.
        var conversationId = HeaderSafe(call.ConversationId ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(conversationId))
            request.Headers.TryAddWithoutValidation(ConversationIdHeader, conversationId);

        // Written the same way and read under the same rule, but it names a conversation OUTSIDE the
        // person the turn is for — so whether it is honoured is the assistant's decision about this leaf,
        // not this writer's. Sending it is a request; the receiver is the boundary.
        var room = HeaderSafe(call.Room ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(room))
            request.Headers.TryAddWithoutValidation(RoomHeader, room);
    }

    /// <summary>
    /// Drops control characters, so a user-controlled value can never split a header.
    /// </summary>
    public static string HeaderSafe(string value) =>
        string.IsNullOrEmpty(value) || !value.Any(char.IsControl)
            ? value
            : new string(value.Where(c => !char.IsControl(c)).ToArray());
}
