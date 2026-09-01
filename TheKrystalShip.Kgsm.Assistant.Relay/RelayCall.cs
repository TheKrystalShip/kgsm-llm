namespace TheKrystalShip.Kgsm.Assistant.Relay;

/// <summary>
/// The per-call parts of a relayed turn — the things that vary between one turn and the next, as
/// opposed to the calling leaf, which is fixed for the life of the client.
/// </summary>
/// <param name="AutoAct">
/// The person's own auto-accept preference for this turn. When true the assistant runs lifecycle
/// commands immediately instead of staging them — but only if the tier it resolved for them allows it,
/// because this is a preference riding a permission and never a permission. Anything but true is
/// propose-only.
/// </param>
/// <param name="ConversationId">
/// A sub-scope of <em>this user's</em> memory, partitioning their own history into separate context
/// windows — a "new chat" in a web client, a channel in Discord. It is not an identity and can never
/// reach another person: the assistant always prefixes the verified user id. Absent leaves the
/// caller on their single, unpartitioned conversation.
/// </param>
/// <param name="Room">
/// A conversation several people hold in common — a Discord thread — identified by the place it
/// happens in rather than by anyone in it. Everyone who speaks there continues the same transcript,
/// and each of them still acts with the authority their own account carries: a shared conversation is
/// not shared authority.
/// <para>
/// It supersedes <see cref="ConversationId"/> when both are sent, because the two answer the same
/// question — <em>which conversation is this?</em> — and only one of them can be the answer. Honoured
/// only for a leaf the assistant permits to open rooms; anything else is read as absent, leaving the
/// caller on their own per-user memory.
/// </para>
/// </param>
public sealed record RelayCall(bool AutoAct = false, string? ConversationId = null, string? Room = null);
