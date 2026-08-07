using TheKrystalShip.Kgsm.Assistant.Infrastructure;

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
    public const string Bot = "kgsm-bot";

    /// <summary>The Control Panel API, which relays the browser chat dock.</summary>
    public const string Api = "kgsm-api";

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
}
