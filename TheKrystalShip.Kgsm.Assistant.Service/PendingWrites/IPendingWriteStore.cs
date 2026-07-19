namespace TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;

/// <summary>
/// Server-side staging for a <c>write_file</c> confirmation's file body, so it never has to ride the
/// stateless HMAC confirmation token (a 10 MB body is untenable there — see
/// <c>ConfirmationTokenService</c>). At token-mint time the Service <see cref="Put"/>s the real content
/// and mints the token from a confirmation whose <c>ConfigValue</c> is the returned opaque id instead of
/// the content; at <c>/confirm</c> it <see cref="TryTake"/>s the id back to the real content before
/// calling <see cref="IServerAssistant.ConfirmAsync"/>. <see cref="ServerAssistant"/> itself never learns
/// this store exists — it always sees real content.
/// </summary>
public interface IPendingWriteStore
{
    /// <summary>Stages <paramref name="content"/>, returning an opaque single-use id that expires at
    /// <paramref name="expiry"/>.</summary>
    string Put(string content, DateTimeOffset expiry);

    /// <summary>
    /// Single-use: on a hit, deletes the row and returns the content via <paramref name="content"/>.
    /// Returns <see langword="false"/> (with an empty <paramref name="content"/>) for an unknown id, an
    /// already-taken id, or one past its expiry (an expired row found here is deleted too, not left to
    /// linger for the next sweep).
    /// </summary>
    bool TryTake(string id, out string content);
}
