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
    string Put(PendingConfirmation confirmation, string userId, DateTimeOffset expiry);

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
}
