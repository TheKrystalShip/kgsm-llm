using NSubstitute;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// How these tests answer "what may this caller do".
/// </summary>
/// <remarks>
/// The service asks an <see cref="IAuthorityProvider"/> for a tier, so that is what a test stubs, and
/// a tier is the whole of what there is to say: authority is the caller's KGSM account and nothing
/// about a chat server enters into it.
/// </remarks>
internal static class AuthStubs
{
    /// <summary>
    /// Stub the authority as the account store would answer it. The seam is substituted for both
    /// halves of the sign-in, so the cast reaches the same object the handler resolves.
    /// </summary>
    public static void StubTier(ISignInService seam, KgsmTier tier) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns(_ => tier);

    /// <summary>
    /// Stub the authority as unreachable. This is an outage, and every caller must keep reporting it as
    /// one — a denial would tell an operator they lost a role they still hold.
    /// </summary>
    public static void StubTierUnreachable(ISignInService seam) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns<KgsmTier>(_ => throw new DiscordAuthException("Discord unreachable."));
}
