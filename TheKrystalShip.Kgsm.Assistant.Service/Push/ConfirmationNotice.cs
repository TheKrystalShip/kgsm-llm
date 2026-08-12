using System.Text.Json;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <summary>
/// What a waiting action reads as on a lock screen, and the payload that carries it.
/// </summary>
/// <remarks>
/// The verb comes from <see cref="ConfirmationKinds.Verb"/> rather than a table of this file's own.
/// Every surface that names an action already says "restart" and "back up" the same way, and a second
/// list is a second thing to keep in step for no gain — a notification that calls it something the
/// chat does not is a notification about a different action as far as the reader is concerned.
/// </remarks>
internal static class ConfirmationNotice
{
    /// <summary>The one line that has to make sense on a locked phone.</summary>
    /// <remarks>
    /// It names the operation and its target, because those are what somebody deciding needs and they
    /// are what fits. What it never carries is the payload — a config value, a file body, a blueprint
    /// draft — both because none of it fits and because a notification is delivered through a push
    /// service and rendered by an OS, neither of which is somewhere a server's contents belong.
    /// </remarks>
    public static string Title(PendingConfirmation confirmation)
    {
        var verb = ConfirmationKinds.Verb(confirmation.Kind);
        var sentence = char.ToUpperInvariant(verb[0]) + verb[1..];

        // Install stages a blueprint on Target with the new instance's optional name on InstanceName,
        // so the thing being named is what the person will end up with, not what it was built from.
        var target = confirmation.Kind == ConfirmationKind.Install
            ? confirmation.InstanceName ?? confirmation.Target
            : confirmation.Target;

        return $"{sentence} {target}?";
    }

    /// <summary>
    /// The body: what is being asked, and by when.
    /// </summary>
    /// <remarks>
    /// The deadline is stated because it is short and real. A staged action lives five minutes by
    /// default, and a notification that hides that is one somebody reads at leisure and acts on too
    /// late — having been told nothing that would have made them hurry.
    /// </remarks>
    public static string Body(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var left = expiresAt - now;
        if (left <= TimeSpan.Zero)
            return "This has expired.";

        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        var window = minutes <= 1 ? "under a minute" : $"about {minutes} minutes";
        return $"The assistant staged this and is waiting on you — you have {window} to decide.";
    }

    /// <summary>
    /// The encrypted body the service worker parses.
    /// </summary>
    /// <remarks>
    /// <paramref name="confirmHandle"/> and <paramref name="cancelHandle"/> are capabilities, which is
    /// the reason this travels encrypted end to end: RFC 8291 means the push service routing it holds
    /// ciphertext it cannot read. The <c>tag</c> is the confirmation's handle so a second notification
    /// about the same action replaces the first rather than stacking under it.
    /// </remarks>
    public static byte[] Payload(
        WaitingConfirmation waiting,
        string confirmHandle,
        string cancelHandle,
        DateTimeOffset now)
    {
        var payload = new
        {
            kind = "confirmation",
            title = Title(waiting.Confirmation),
            body = Body(waiting.ExpiresAt, now),
            expiresAt = waiting.ExpiresAt.ToString("O"),
            tag = "kgsm-confirmation:" + waiting.Handle,
            confirm = confirmHandle,
            cancel = cancelHandle,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }
}
