namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// The namespaces this service stores conversations under, and the difference between them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Web"/> keys a conversation to a <em>person</em>: <c>web:{userId}[:{chatId}]</c>, with
/// the user id set server-side from the verified identity. A client can name a chat of its own but
/// never anybody else's, because the part that identifies whose memory it is was never the client's
/// to write.
/// </para>
/// <para>
/// <see cref="Room"/> keys one to a <em>place</em>: <c>room:{room}</c>, with no user segment. Several
/// people speak into the same transcript, which is the point and also why it cannot be made safe the
/// way the other one is — there is no verified id in the key to anchor it. What replaces that anchor
/// is a leaf allow-list on the relay path (<see cref="RelayLeaves.OpensRooms"/>); it is the only
/// route by which a room key comes into existence.
/// </para>
/// <para>
/// Two constants in one place so the difference is legible where the keys are composed. They are not
/// interchangeable: a room read as a web scope would put a shared transcript inside one person's
/// history, and a web scope read as a room would offer one person's history to a channel.
/// </para>
/// </remarks>
internal static class ConversationSurfaces
{
    /// <summary>One person's own memory, partitioned into their own chats.</summary>
    public const string Web = "web";

    /// <summary>One place's shared memory, held in common by everyone who speaks there.</summary>
    public const string Room = "room";

    /// <summary>Every surface, for the review tooling that reads across them.</summary>
    public static readonly IReadOnlyList<string> All = [Web, Room];

    /// <summary>
    /// The surface <paramref name="name"/> names, or <see langword="null"/> when it names none.
    /// Matched against the list rather than accepted as given: a review route takes this from a query
    /// string, and an unrecognised surface must be refused rather than turned into a key prefix that
    /// selects nothing and reports it as an empty corpus.
    /// </summary>
    public static string? Resolve(string? name) =>
        name is null ? null : All.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The room this request is speaking into, or <see langword="null"/> when it is not in one. Read
    /// from what the auth filter admitted, never from the header — a room key exists only when a leaf
    /// on the allow-list asked for one.
    /// </summary>
    public static string? RoomOf(HttpContext http) =>
        ConversationScope.Sanitize(
            http.Items.TryGetValue(BearerAuthFilter.RelayRoomKey, out var room) && room is string named
                ? named
                : null);

    /// <summary>
    /// The stored key a request addresses: the room it is speaking into, or the caller's own chat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One resolution for every route.</b> A turn, a compaction and a wipe have to name the same
    /// conversation or they are not operating on the same thing — and the way that goes wrong is silent:
    /// a room asked to compact would fold the caller's private chat instead and report that it worked.
    /// </para>
    /// <para>
    /// <b>A room wins over a per-chat id</b>, rather than combining with it. The two answer the same
    /// question, and a key built from both would be a room per person — everybody alone in a transcript
    /// named after the place they thought they were sharing.
    /// </para>
    /// </remarks>
    public static string Key(HttpContext http, string userId, string? chatScope) =>
        RoomOf(http) is { } room
            ? $"{Room}:{room}"
            : string.IsNullOrEmpty(chatScope)
                ? $"{Web}:{userId}"
                : $"{Web}:{userId}:{chatScope}";
}
