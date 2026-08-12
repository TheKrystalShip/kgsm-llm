using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Streaming;
using TheKrystalShip.KGSM.WebPush;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <summary>
/// Announces a staged action to the phone of the person it is waiting on, once they have stopped
/// looking at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger is presence, not a delay.</b> <see cref="IConversationEventBus.PresentWithin"/>
/// already answers "is this person at a surface" and is what decides whether a turn nobody is watching
/// keeps running. A dwell timer here would be a second, worse notion of the same thing: worse because
/// it would fire on somebody sitting in front of the chat, and because the two would drift.
/// </para>
/// <para>
/// <b>Nothing is given up on.</b> A confirmation whose owner is present is left exactly as it was and
/// re-examined on the next pass — somebody at their desk when it was staged may walk away with three
/// minutes still on the clock, and that is the case this exists for. What ends it is the confirmation
/// expiring, which drops it out of <see cref="IPendingConfirmationStore.Unannounced"/> on its own.
/// </para>
/// <para>
/// <b>It announces, it does not decide.</b> The buttons carry handles; redeeming one re-derives the
/// person's authority exactly as <c>/confirm</c> does. This worker never checks whether somebody may
/// perform the action it is telling them about, because by the time they tap it the answer may have
/// changed, and the answer that matters is the one at the tap.
/// </para>
/// </remarks>
internal sealed class ConfirmationPushWorker(
    IPendingConfirmationStore pending,
    IPushSubscriptionStore subscriptions,
    IPushActionStore actions,
    IConversationEventBus bus,
    WebPushSender sender,
    IOptions<AssistantServiceOptions> options,
    TimeProvider clock,
    ILogger<ConfirmationPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var push = options.Value.Push;
        if (!push.Enabled)
        {
            logger.LogInformation(
                "Confirmation push is off (Assistant__Push__Enabled=false); staged actions are announced nowhere.");
            return;
        }

        var period = TimeSpan.FromSeconds(Math.Max(push.PollSeconds, 1));
        using var timer = new PeriodicTimer(period, clock);

        logger.LogInformation(
            "Confirmation push is on: waiting actions are announced once their owner has been away for {Grace}s.",
            push.PresenceGraceSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;

                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad pass must not end the loop: everything it looks at is short-lived, so a
                // worker that stops means silence from here on rather than one missed notification.
                logger.LogError(ex, "Confirmation push pass failed; retrying on the next tick.");
            }
        }
    }

    /// <summary>One pass over everything waiting. Internal so a test can drive it against a clock.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        var push = options.Value.Push;
        var grace = TimeSpan.FromSeconds(Math.Max(push.PresenceGraceSeconds, 0));

        foreach (var waiting in pending.Unannounced())
        {
            if (ct.IsCancellationRequested)
                return;

            // They are at a surface, and every surface shows the proposal inline. Leave it standing.
            if (bus.PresentWithin(waiting.Stager.UserId, grace))
                continue;

            var devices = subscriptions.For(waiting.Stager.UserId);
            if (devices.Count == 0)
                continue;

            if (await AnnounceAsync(waiting, devices, push.Subject, ct).ConfigureAwait(false))
                pending.MarkAnnounced(waiting.Handle);
        }
    }

    /// <summary>
    /// Push one waiting action to every device its owner registered.
    /// </summary>
    /// <returns>
    /// Whether any device accepted it. False leaves the row unannounced, so a push service having a
    /// bad minute costs a retry rather than the notification.
    /// </returns>
    private async Task<bool> AnnounceAsync(
        WaitingConfirmation waiting,
        IReadOnlyList<StoredSubscription> devices,
        string subject,
        CancellationToken ct)
    {
        var keys = subscriptions.Keys();
        var now = clock.GetUtcNow();
        var delivered = false;

        foreach (var device in devices)
        {
            // A handle per device per button. Handing every device the same pair would mean retiring
            // one browser voids the buttons on another, and would make "which device acted" a question
            // with no answer.
            var endpoint = device.Subscription.Endpoint;
            var confirm = actions.Mint(
                PushActionVerb.Confirm, waiting.Handle, waiting.Stager, endpoint, waiting.ExpiresAt);
            var cancel = actions.Mint(
                PushActionVerb.Cancel, waiting.Handle, waiting.Stager, endpoint, waiting.ExpiresAt);

            var payload = ConfirmationNotice.Payload(waiting, confirm, cancel, now);
            var result = await sender
                .SendAsync(device.Subscription, payload, keys, subject, ct)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case PushOutcome.Accepted:
                    delivered = true;
                    // ⚠ Accepted by the push service is NOT shown on a phone, and this says only the
                    // first. What happens after the handoff is the OS's and is not observable here.
                    logger.LogDebug(
                        "Handed a waiting {Kind} for {User} to the push service.",
                        waiting.Confirmation.Kind, waiting.Stager.UserId);
                    break;

                case PushOutcome.Expired:
                    logger.LogInformation(
                        "Retiring a push subscription for {User}: {Reason}", waiting.Stager.UserId, result.Error);
                    subscriptions.Retire(endpoint);
                    break;

                default:
                    logger.LogWarning(
                        "Could not announce a waiting {Kind} to a device of {User} ({Status}): {Error}",
                        waiting.Confirmation.Kind, waiting.Stager.UserId, result.Status, result.Error);
                    // The handles minted for this attempt are left standing: the next pass mints its
                    // own, and both die with the confirmation they point at within the minute.
                    break;
            }
        }

        return delivered;
    }
}
