namespace TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;

/// <summary>
/// Server-side staging for every action the assistant proposes: the resolved operation is held here
/// and only an opaque handle is given out, which the client hands back to <c>/confirm</c>.
/// </summary>
/// <remarks>
/// <para>
/// One model for every surface. A client is told <em>that</em> something is awaiting approval and is
/// given a handle to approve it with; <em>what</em> would be done never leaves this process. The
/// alternative — handing the operation to the client inside a signed envelope — is sound and was
/// what this did, but it puts a floor under the handle's size that a browser does not care about and
/// a Discord button cannot meet at all: its <c>customId</c> caps at 100 characters, and a signed
/// operation runs past that before the config value it carries is anything but trivial. A handle is
/// 32 characters whatever the operation, so no surface has to work around the encoding of another.
/// </para>
/// <para>
/// The handle <b>is</b> the capability, so it is minted from a cryptographic RNG and never derived
/// from the operation. What keeps it from being enough on its own is that <c>/confirm</c> re-derives
/// the caller's authority at the click and re-validates the target against live inventory, and this
/// store records who staged it so it can only be redeemed by them.
/// </para>
/// <para>
/// Redemption is single-use. A staged action approved twice — a double-clicked button, a retried
/// request — is a second execution of something the user asked for once.
/// </para>
/// </remarks>
public interface IPendingConfirmationStore
{
    /// <summary>
    /// Stages <paramref name="confirmation"/> for <paramref name="userId"/>, returning the opaque
    /// handle that redeems it until <paramref name="expiry"/>.
    /// </summary>
    /// <param name="announceTo">
    /// Who to notify if they walk away from this one, or <see langword="null"/> to announce it nowhere.
    /// <para>
    /// Eligibility and recipient are one parameter because they are one fact: there is no such thing as
    /// an announceable action with nobody to announce it to. It is supplied by the shared-turn
    /// streaming path and nothing else, because that is the browser surfaces' path — an action staged
    /// through the buffered <c>/turn</c> is kgsm-bot's, and Discord already renders Confirm and Cancel
    /// on it. Pushing that one too notifies one person twice about one decision, from two apps, with
    /// two sets of buttons that race each other.
    /// </para>
    /// <para>
    /// It carries the whole identity rather than the user id alone because a notification's button
    /// arrives with no session, so the account has to be rebuilt from what was recorded here before its
    /// authority can be re-derived.
    /// </para>
    /// </param>
    /// <param name="conversationId">
    /// The conversation it was proposed in, so it can be restated to a surface that comes back to it.
    /// <para>
    /// ⚠ Without this a proposal exists only as a live stream frame, and any fresh load — a reload, a
    /// second device, or following the notification that announced it — shows the assistant saying it
    /// staged something with no way to approve it. The endpoints own the state; the stream is an
    /// optimisation over them.
    /// </para>
    /// </param>
    string Put(
        PendingConfirmation confirmation,
        string userId,
        DateTimeOffset expiry,
        ConfirmationStager? announceTo = null,
        string? conversationId = null);

    /// <summary>
    /// Redeems <paramref name="id"/> on behalf of <paramref name="userId"/>, yielding the operation
    /// it was staged for.
    /// </summary>
    /// <remarks>
    /// The store enforces who a handle belongs to, rather than handing the operation back and asking
    /// the caller to check. It also decides what a mismatch costs: a handle presented by anyone but
    /// the user it was staged for is <b>left standing</b>, because consuming it would let a wrong
    /// guess destroy an action somebody else is about to approve.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> for an unknown handle, one already redeemed, one past its expiry, and
    /// one belonging to somebody else alike — a caller cannot tell which, and does not need to: none
    /// of them is an action to run.
    /// </returns>
    bool TryTake(string? id, string userId, out PendingConfirmation confirmation);

    /// <summary>
    /// Actions that are still waiting, may be announced, and have not been announced yet.
    /// </summary>
    /// <remarks>
    /// Expired rows are excluded: announcing one would produce a notification whose buttons are dead
    /// before it is drawn.
    /// </remarks>
    IReadOnlyList<WaitingConfirmation> Unannounced();

    /// <summary>
    /// Record that <paramref name="id"/> has been announced, so it is announced once.
    /// </summary>
    /// <remarks>
    /// Marked after the push is accepted rather than before it is attempted: a send that fails leaves
    /// the row standing to be retried on the next pass, which is the behaviour a transient push-service
    /// outage needs. The cost of the other order is silence.
    /// </remarks>
    void MarkAnnounced(string id);

    /// <summary>
    /// What <paramref name="userId"/> still has awaiting approval in <paramref name="conversationId"/>.
    /// </summary>
    /// <remarks>
    /// Read back on every conversation load, because a staged action is state rather than an event: the
    /// frame that announced it reaches only the surfaces attached when it was staged, and the one that
    /// most needs it is the one arriving afterwards. Expired rows are excluded — restating a proposal
    /// whose handle is already dead offers a button that cannot work.
    /// </remarks>
    IReadOnlyList<StagedProposal> PendingFor(string userId, string conversationId);
}

/// <summary>A proposal still awaiting its owner, as a conversation load restates it.</summary>
public sealed record StagedProposal(
    string Handle,
    PendingConfirmation Confirmation,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Who staged an action, in full — enough to ask what they may do without a session to read it from.
/// </summary>
/// <param name="Provider">Which identity provider proved them, which the account lookup is keyed by.</param>
/// <param name="UserId">Their id with that provider. What <see cref="IPendingConfirmationStore.TryTake"/> matches on.</param>
/// <param name="DisplayName">
/// What to attribute the action to. A snapshot, and the only field here that is: it labels an
/// invocation, where the two above decide authority and are re-resolved against the live account store
/// every time.
/// </param>
public sealed record ConfirmationStager(string Provider, string UserId, string DisplayName);

/// <summary>A staged action waiting on somebody, with what an announcement needs to describe it.</summary>
public sealed record WaitingConfirmation(
    string Handle,
    ConfirmationStager Stager,
    PendingConfirmation Confirmation,
    DateTimeOffset ExpiresAt);
