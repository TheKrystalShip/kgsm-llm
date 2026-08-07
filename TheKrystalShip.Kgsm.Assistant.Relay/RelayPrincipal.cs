using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Relay;

/// <summary>
/// The verified end-user a relay call acts for: who they are, and what the calling leaf resolved
/// they may do.
/// </summary>
/// <remarks>
/// Identity and authority travel together deliberately. Kept as separate arguments, a call site can
/// forward a person while forgetting — or hand-picking — what they were allowed to do. Bundled, a
/// caller cannot express "this user, with somebody else's authority".
/// </remarks>
/// <param name="UserId">The Discord snowflake the calling leaf authenticated. Never free text.</param>
/// <param name="DisplayName">
/// The name to show for them. User-controlled and crossing a trust boundary, so it is stripped of
/// control characters before it reaches a header.
/// </param>
/// <param name="Tier">
/// The tier read from the caller's own verified session. The assistant trusts it because the relay
/// secret matched, so it must never be anything but what the calling leaf measured.
/// </param>
public sealed record RelayPrincipal(string UserId, string DisplayName, KgsmTier Tier);

/// <summary>
/// The per-call parts of a relayed turn — the things that vary between one turn and the next, as
/// opposed to <see cref="RelayPrincipal"/>, which varies between one person and the next.
/// </summary>
/// <param name="AutoAct">
/// The calling leaf's auto-accept decision: its verified <em>admin</em> tier ∧ the user's per-turn
/// toggle. When true the assistant runs lifecycle commands immediately instead of staging them.
/// Strictly stronger than the tier alone, which is why it is its own value — it is a preference
/// riding a permission, not a permission. Anything but true is propose-only.
/// </param>
/// <param name="ConversationId">
/// A sub-scope of <em>this user's</em> memory, partitioning their own history into separate context
/// windows — a "new chat" in a web client, a channel in Discord. It is not an identity and can never
/// reach another person: the assistant always prefixes the verified user id. Absent leaves the
/// caller on their single, unpartitioned conversation.
/// </param>
public sealed record RelayCall(bool AutoAct = false, string? ConversationId = null);
