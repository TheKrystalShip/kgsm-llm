using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Relay;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Maps the leaf named by <c>X-Relay-Leaf</c> onto the audit origin its surface records under.
/// </summary>
/// <remarks>
/// <para>
/// A table rather than a string copy, deliberately. The leaf name says <em>who is calling</em>;
/// the origin says <em>which surface the person was using</em>, and those are different facts —
/// kgsm-api calls on behalf of a browser chat, whose origin is the assistant, not the api. Deriving
/// one from the other by convention would encode a coincidence.
/// </para>
/// <para>
/// It also means a leaf cannot name its own audit origin. Adding a leaf that talks to the assistant
/// costs a row here, and that is the point: a caller that declares its own origin is a caller that
/// can misdeclare it, and the engine's journal would record the claim faithfully.
/// </para>
/// <para>
/// An unknown or absent leaf resolves to <see cref="Invocation.AssistantOrigin"/> — what every
/// relayed turn recorded before any leaf named itself — so a caller that does not speak the header
/// is unaffected by it.
/// </para>
/// </remarks>
internal static class RelayLeaves
{
    /// <summary>The Discord bot. Its surface is Discord, so its actions are Discord's.</summary>
    public const string Bot = RelayLeaf.Bot;

    /// <summary>The Control Panel API, which relays the browser chat dock.</summary>
    public const string Api = RelayLeaf.Api;

    // Keyed by the same constants the senders write, so the row that decides an audit origin and the
    // header that selects it cannot come to mean different leaves.
    private static readonly IReadOnlyDictionary<string, string> Origins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Bot] = "discord",
            [Api] = Invocation.AssistantOrigin,
        };

    /// <summary>
    /// The audit origin for <paramref name="leaf"/>, falling back to
    /// <see cref="Invocation.AssistantOrigin"/> for an absent or unrecognised one.
    /// </summary>
    public static string OriginFor(string? leaf) =>
        leaf is not null && Origins.TryGetValue(leaf, out var origin) ? origin : Invocation.AssistantOrigin;

    /// <summary>
    /// The leaves permitted to open a <b>room</b> — a conversation keyed to a place rather than to a
    /// person, which everyone there shares.
    /// </summary>
    /// <remarks>
    /// A table for the same reason the origins above are one: a caller that decides its own answer can
    /// decide it wrongly. Every other conversation on this service is prefixed with the verified user
    /// id, which is what makes a caller structurally unable to name somebody else's memory; a room is
    /// the one shape without that anchor, so what stands in its place is this list plus the relay
    /// secret that got the request here.
    /// <para>
    /// Only the Discord bot is on it. A room needs a place with a membership the host can see and a
    /// life of its own — a thread has both. The Control Panel relays one browser session at a time and
    /// has nothing to key a room to, so a room claim from it is a bug in the making rather than a
    /// feature waiting for a caller.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<string> RoomOpeners =
        new HashSet<string>(StringComparer.Ordinal) { Bot };

    /// <summary>
    /// Whether <paramref name="leaf"/> may open a room. False for an absent, unrecognised or
    /// unlisted leaf — a room is granted, never assumed.
    /// </summary>
    public static bool OpensRooms(string? leaf) => leaf is not null && RoomOpeners.Contains(leaf);
}
