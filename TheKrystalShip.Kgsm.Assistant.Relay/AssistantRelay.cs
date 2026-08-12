using TheKrystalShip.KGSM.Auth;

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
/// Writes the trusted-relay headers for a call made on a verified end-user's behalf.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for every leaf that relays a turn. These headers carry <em>who someone is and
/// what they may do</em>, and a second hand-rolled copy is how two surfaces come to disagree about
/// that — the one disagreement an authority header cannot survive. Each consumer still calls
/// whichever endpoints it needs and shapes the responses its own way; what must not diverge is the
/// identity, not the routes.
/// </para>
/// <para>
/// The secret and the leaf name are fixed at construction rather than passed per call, so a request
/// cannot be sent half-identified: every call this writes is from the same leaf, on behalf of
/// whoever it names.
/// </para>
/// <para>
/// The assistant parses each of these fail-closed. An absent or unrecognised tier is
/// <see cref="KgsmTier.None"/>, an auto-accept that is not exactly <c>true</c> is propose-only, and
/// an unrecognised leaf reads the assistant's own prompts under its own origin — so a header this
/// writer omits can never grant anything by omission.
/// </para>
/// </remarks>
public sealed class AssistantRelay
{
    /// <summary>The shared secret that authenticates the relay itself, before any identity is read.</summary>
    public const string SecretHeader = "X-Relay-Secret";

    /// <summary>The forwarded user's id — the authoritative prefix of their memory namespace.</summary>
    public const string UserHeader = "X-Relay-User";

    /// <summary>The forwarded user's display name.</summary>
    public const string UserNameHeader = "X-Relay-User-Name";

    /// <summary>The tier the calling leaf verified for them.</summary>
    public const string TierHeader = "X-Relay-Tier";

    /// <summary>The calling leaf's auto-accept decision for this turn.</summary>
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

    private readonly string _secret;
    private readonly string _leaf;

    /// <param name="secret">
    /// The shared relay secret. Blank disables the relay path — the assistant then falls through to
    /// its session-bearer path and answers <c>401</c>, rather than treating an unauthenticated call
    /// as trusted.
    /// </param>
    /// <param name="leaf">Which leaf this is; use a <see cref="RelayLeaf"/> constant.</param>
    public AssistantRelay(string? secret, string leaf)
    {
        _secret = secret ?? string.Empty;
        _leaf = leaf ?? string.Empty;
    }

    /// <summary>Whether a secret is configured, and so whether the relay path is usable at all.</summary>
    public bool IsConfigured => _secret.Length > 0;

    /// <summary>
    /// Writes the relay headers onto <paramref name="request"/>: the shared secret, the forwarded
    /// identity and its verified tier, this leaf's name, and the per-call parts when given.
    /// </summary>
    public void Write(HttpRequestMessage request, RelayPrincipal caller, RelayCall? call = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (_secret.Length > 0)
            request.Headers.TryAddWithoutValidation(SecretHeader, _secret);

        // The user id is set at login, never free text; the display name is user-controlled and
        // crossing a trust boundary, so both are stripped of control characters — defence in depth
        // against header injection, and it keeps an odd display name from throwing.
        request.Headers.TryAddWithoutValidation(UserHeader, HeaderSafe(caller.UserId));

        // Whitespace-only counts as absent, matching how the assistant reads it — it falls back to
        // the user id for a blank name. A writer and a reader disagreeing about what "blank" means is
        // how an empty header comes to mean something.
        var displayName = HeaderSafe(caller.DisplayName);
        if (!string.IsNullOrWhiteSpace(displayName))
            request.Headers.TryAddWithoutValidation(UserNameHeader, displayName);

        request.Headers.TryAddWithoutValidation(TierHeader, KgsmTiers.ToWire(caller.Tier));

        if (_leaf.Length > 0)
            request.Headers.TryAddWithoutValidation(LeafHeader, _leaf);

        if (call is null)
            return;

        request.Headers.TryAddWithoutValidation(AutoActHeader, call.AutoAct ? "true" : "false");

        // Same rule: the assistant sanitises this to [A-Za-z0-9_-] and treats what is left of a blank
        // value as no sub-scope at all, so there is nothing for a whitespace-only header to say.
        var conversationId = HeaderSafe(call.ConversationId ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(conversationId))
            request.Headers.TryAddWithoutValidation(ConversationIdHeader, conversationId);

        // Written the same way and read under the same rule, but it names a conversation OUTSIDE this
        // user — so whether it is honoured is the assistant's decision about this leaf, not this
        // writer's. Sending it is a request; the receiver is the boundary.
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
