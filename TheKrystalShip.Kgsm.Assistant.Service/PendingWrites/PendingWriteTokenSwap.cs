using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

namespace TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;

/// <summary>
/// Swaps a staged <c>write_file</c> confirmation's real file content for an opaque pending-write id
/// BEFORE it's handed to <c>ConfirmationTokenService.Create</c> — a 10 MB body can't ride a stateless
/// HMAC token (Contracts.cs / the write-file plan). Every other kind passes through unchanged.
/// <see cref="ConfirmationTokenService"/> itself needs no change: it only ever signs whatever string it's
/// given, so it stays oblivious to this swap. Shared by both mint points (the buffered <c>/turn</c>
/// response and the SSE <c>command.proposed</c> frame) so they can't drift apart.
/// </summary>
internal static class PendingWriteTokenSwap
{
    public static PendingConfirmation ForToken(
        PendingConfirmation confirmation, IPendingWriteStore store, int ttlSeconds)
    {
        if (confirmation.Kind != ConfirmationKind.WriteFile || confirmation.ConfigValue is null)
            return confirmation;

        var expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(ttlSeconds, 1));
        var pendingId = store.Put(confirmation.ConfigValue, expiry);
        // ConfigKey stays the real relative path (small, safe to sign/display); only the
        // potentially-large ConfigValue (the file body) is replaced by the opaque id.
        return confirmation with { ConfigValue = pendingId };
    }
}
