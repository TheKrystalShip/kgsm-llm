using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.WebPush;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <summary>
/// Runs an action approved from a notification, and sends the verdict back to the device that
/// approved it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detached, because the caller cannot wait.</b> A confirmed action runs to completion —
/// <c>instances create-backup</c> on a large world is minutes, and the executor allows fifteen. The
/// redemption route is called by a service worker woken by a push: browsers give one a short, unstated
/// budget and terminate it, so a request held open for the length of a backup returns to nothing at
/// all. The person taps Confirm, sees no answer, and reasonably concludes it did not work — while the
/// backup runs.
/// </para>
/// <para>
/// The chat has the same problem and solves it by streaming progress over SSE. A notification has no
/// stream, so the verdict comes back the way the question went out: as a second push, to the same
/// device. Nothing is claimed before it is known — the immediate answer says the action was approved
/// and started, which is true, and the outcome says what happened when there is an outcome to state.
/// </para>
/// <para>
/// ⚠ Everything here runs OUTSIDE the request: its own DI scope (the assistant and its provenance are
/// scoped, and the request's are disposed the moment the response is written) and the application's
/// lifetime token rather than the request's, which is cancelled as soon as the response completes.
/// </para>
/// </remarks>
internal sealed class PushConfirmationRunner(
    IServiceScopeFactory scopes,
    IPushSubscriptionStore subscriptions,
    WebPushSender sender,
    IOptions<AssistantServiceOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<PushConfirmationRunner> logger)
{
    /// <summary>
    /// Start <paramref name="confirmation"/> on behalf of the person who approved it from
    /// <paramref name="action"/>'s device, and notify that device when it settles.
    /// </summary>
    public void Start(PendingConfirmation confirmation, PushAction action, bool canPerform)
    {
        _ = Task.Run(() => RunAsync(confirmation, action, canPerform), CancellationToken.None);
    }

    private async Task RunAsync(PendingConfirmation confirmation, PushAction action, bool canPerform)
    {
        var ct = lifetime.ApplicationStopping;
        var verb = ConfirmationKinds.Verb(confirmation.Kind);

        string title;
        string body;
        try
        {
            using var scope = scopes.CreateScope();
            var assistant = scope.ServiceProvider.GetRequiredService<IServerAssistant>();
            var invocation = scope.ServiceProvider.GetRequiredService<IInvocationContext>();

            // Attribute it to the person who approved it, established INSIDE the run: the request that
            // carried the tap is gone by now, and provenance is ambient per-scope.
            using var provenance = invocation.Begin(
                Invocation.ForAssistant(action.Stager.DisplayName, RelayLeaves.OriginFor(null)));

            var outcome = await assistant.ConfirmAsync(confirmation, canPerform, ct).ConfigureAwait(false);

            title = outcome.Ok
                ? $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {confirmation.Target} — done"
                : $"Couldn't {verb} {confirmation.Target}";
            body = outcome.Summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is shutting down mid-action. ⚠ Say that rather than reporting a failure: the
            // command may well have completed, and we no longer have any way to find out.
            title = $"Couldn't confirm the {verb}";
            body = "The assistant restarted while this was running — check the server before retrying.";
            logger.LogWarning(
                "Shutdown interrupted a {Kind} confirmed from a notification.", confirmation.Kind);
        }
        catch (Exception ex)
        {
            title = $"Couldn't {verb} {confirmation.Target}";
            body = "The action failed. Open the assistant for the detail.";
            logger.LogError(
                ex, "A {Kind} confirmed from a notification failed.", confirmation.Kind);
        }

        await NotifyAsync(action, title, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the verdict to the one device that approved it.
    /// </summary>
    /// <remarks>
    /// Only that device, not every device the person owns: the others were never told the question, so
    /// an answer would arrive at them with nothing to be an answer to.
    /// </remarks>
    private async Task NotifyAsync(PushAction action, string title, string body, CancellationToken ct)
    {
        var device = subscriptions.For(action.Stager.UserId)
            .FirstOrDefault(d => string.Equals(
                d.Subscription.Endpoint, action.Endpoint, StringComparison.Ordinal));

        if (device is null)
        {
            // Unsubscribed between approving and finishing. The action still ran; there is simply
            // nowhere to say so, and inventing a delivery would be worse than the silence.
            logger.LogInformation(
                "No device left to report a confirmed action to for {User}.", action.Stager.UserId);
            return;
        }

        var payload = ConfirmationNotice.OutcomePayload(title, body, action.ConfirmationHandle);
        var result = await sender
            .SendAsync(device.Subscription, payload, subscriptions.Keys(), options.Value.Push.Subject, ct)
            .ConfigureAwait(false);

        if (result.Outcome == PushOutcome.Expired)
            subscriptions.Retire(action.Endpoint);
        else if (result.Outcome == PushOutcome.Failed)
            logger.LogWarning(
                "Could not report a confirmed action to {User} ({Status}): {Error}",
                action.Stager.UserId, result.Status, result.Error);
    }
}
