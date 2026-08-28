using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <summary>Which button a push handle is.</summary>
internal enum PushActionVerb
{
    /// <summary>Approve the staged action.</summary>
    Confirm = 0,

    /// <summary>Discard it, deliberately and now, rather than letting it lapse.</summary>
    Cancel = 1,
}

/// <summary>A redeemed push handle: what it authorises, and for whom.</summary>
internal sealed record PushAction(
    PushActionVerb Verb,
    string ConfirmationHandle,
    ConfirmationStager Stager,
    string Endpoint);

/// <summary>
/// The indirection that lets a notification button act: a second handle, held only by the device the
/// notification reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> A staged action is redeemed by
/// <c>IPendingConfirmationStore.TryTake(handle, userId)</c>, and <c>/confirm</c> reads that
/// <c>userId</c> off the caller's session. A service worker has no session — it wakes on an OS push
/// with no page, no bearer and nothing it could sign with. So the account has to travel with the
/// capability, which means the capability has to be one this host minted for one device.
/// </para>
/// <para>
/// <b>What actually protects it.</b> Not this store: a handle presented here is trusted to name its
/// account, and the only thing keeping it from being a way to act as somebody else is that it is
/// unguessable, single-use, and dead within the lifetime of the confirmation it points at. What
/// re-checks the person is the redemption path, which resolves their authority at the tap exactly as
/// <c>/confirm</c> does — a handle minted while somebody was an operator does not stay one.
/// </para>
/// <para>
/// <b>The endpoint is not authentication.</b> It is recorded, and a tap cannot prove it. Its worth is
/// revocation and provenance: forgetting a device voids what was staged for it, and a handle can be
/// traced to the browser it was sent to. Treating it as a second factor would be believing something
/// the wire never establishes.
/// </para>
/// </remarks>
internal interface IPushActionStore
{
    /// <summary>
    /// Mint a handle authorising <paramref name="verb"/> on <paramref name="confirmationHandle"/> for
    /// <paramref name="stager"/>, from <paramref name="endpoint"/>, until <paramref name="expiry"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="expiry"/> is the <em>confirmation's</em> own expiry and never a longer one of
    /// this handle's. A push handle outliving what it approves is a second lifetime nobody declared,
    /// and it would be one granted by the act of announcing rather than by the act of staging.
    /// </remarks>
    string Mint(
        PushActionVerb verb,
        string confirmationHandle,
        ConfirmationStager stager,
        string endpoint,
        DateTimeOffset expiry);

    /// <summary>
    /// Redeem <paramref name="handle"/>, yielding what it authorises.
    /// </summary>
    /// <remarks>
    /// Single-use and unconditional: the row is consumed whatever the caller does next, because the
    /// only caller is the redemption route and a handle that survived a failed attempt would be a
    /// retry of something that already ran. An unknown handle and an expired one are the same answer —
    /// neither is an action to take.
    /// </remarks>
    bool TryTake(string? handle, out PushAction action);

    /// <summary>
    /// Void every handle staged for <paramref name="endpoint"/>, because that device is gone.
    /// </summary>
    void VoidFor(string endpoint);

    /// <summary>
    /// Void every handle pointing at <paramref name="confirmationHandle"/>, because it has been
    /// settled elsewhere.
    /// </summary>
    /// <remarks>
    /// Approving in the chat leaves the notification's two buttons standing on a confirmation that no
    /// longer exists. They would fail correctly — the underlying handle is already consumed — but
    /// failing correctly is not the same as not being there, and a person tapping Confirm on something
    /// they already confirmed deserves better than a refusal.
    /// </remarks>
    void VoidForConfirmation(string confirmationHandle);
}
