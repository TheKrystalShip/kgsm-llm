using TheKrystalShip.KGSM.WebPush;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <summary>A browser this leaf may push to, as it was registered.</summary>
/// <param name="Subscription">Endpoint and keys, in the shape the sender takes.</param>
/// <param name="UserId">Whose browser it is. A confirmation is announced to its own stager and no one else.</param>
/// <param name="Origin">
/// The page origin that registered it — the standalone assistant today, and the only reason it is
/// recorded is that a second origin is a client-side decision this store must not have an opinion
/// about. Nothing branches on it; it is here so nothing has to be migrated when one does.
/// </param>
internal sealed record StoredSubscription(PushSubscription Subscription, string UserId, string? Origin);

/// <summary>
/// The devices registered for Web Push, and the VAPID identity every one of them was registered
/// against.
/// </summary>
/// <remarks>
/// <para>
/// The key pair and the subscriptions live together because they are one fact: a subscription is
/// bound at creation to the public key the browser was handed, so a new pair does not "rotate"
/// anything — it silently orphans every device already registered, with no error at either end. That
/// is why the pair is generated once, on first use, and read back forever after.
/// </para>
/// <para>
/// It is generated rather than configured for the same reason: a key in an env file is a key a deploy
/// can lose, and losing it looks exactly like the feature quietly not working.
/// </para>
/// </remarks>
internal interface IPushSubscriptionStore
{
    /// <summary>
    /// This host's VAPID pair, generating it on the first call and returning that same pair on every
    /// call after.
    /// </summary>
    VapidKeyPair Keys();

    /// <summary>
    /// Record a browser, replacing any previous registration of the same endpoint.
    /// </summary>
    /// <remarks>
    /// Keyed by endpoint rather than by user: an endpoint is one browser, and a browser that has been
    /// signed into two accounts must end up owned by whoever registered it last, not subscribed twice.
    /// </remarks>
    void Register(string userId, PushSubscription subscription, string? origin);

    /// <summary>Forget a browser at that person's request. Returns whether there was one to forget.</summary>
    bool Unregister(string userId, string endpoint);

    /// <summary>Every browser registered to <paramref name="userId"/>.</summary>
    IReadOnlyList<StoredSubscription> For(string userId);

    /// <summary>
    /// Forget a browser because the push service says it is gone (a <c>404</c> or <c>410</c>), and
    /// void anything staged for it.
    /// </summary>
    /// <remarks>
    /// Not the same call as <see cref="Unregister"/> even though it deletes the same row: this one is
    /// the push service's verdict rather than a person's choice, and it is unconditional because there
    /// is no user to scope it to. Retrying a gone endpoint fails identically forever.
    /// </remarks>
    void Retire(string endpoint);
}
