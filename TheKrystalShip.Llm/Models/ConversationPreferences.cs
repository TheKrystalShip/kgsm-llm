namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The switches a conversation carries into its next turn. Both are nullable and null means
/// <b>unset</b> — the caller applies its own default — never <c>false</c>: a conversation that has
/// never been told anything about thinking must fall to the host's configured default, and reading
/// silence as "off" would override that configuration with a value nobody chose.
/// <para>
/// The same shape serves as a DELTA when writing
/// (<see cref="Interfaces.IConversationStore.SetPreferences"/>), where null means "this write says
/// nothing about that switch" — so the two can be set independently.
/// </para>
/// <para>
/// These live on the CONVERSATION rather than on the user. <see cref="Autorun"/> is the one switch
/// that skips the confirmation gate on a destructive action, and a per-user preference would mean
/// arming it in one place silently arms every other conversation that person holds, including one on
/// another surface weeks later. Scoped here, it applies where it was turned on.
/// </para>
/// </summary>
/// <param name="Think">Whether the assistant reasons step by step before answering.</param>
/// <param name="Autorun">
/// Whether an authorized action runs without a confirmation step. It is <b>intent, not authority</b>:
/// every gate it passes through narrows it further (the caller's tier, and the surface's own floor),
/// so storing <c>true</c> can never grant what the caller could not otherwise do.
/// </param>
public sealed record ConversationPreferences(bool? Think, bool? Autorun)
{
    /// <summary>A conversation that has been told nothing — both switches fall to their defaults.</summary>
    public static readonly ConversationPreferences Unset = new(null, null);
}
