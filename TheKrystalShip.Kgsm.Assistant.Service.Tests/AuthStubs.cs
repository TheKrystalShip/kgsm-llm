using NSubstitute;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// How these tests answer "what may this caller do".
/// </summary>
/// <remarks>
/// <para>
/// The service asks an <see cref="IAuthorityProvider"/> for a tier, so that is what a test stubs. The
/// stubs here take a <em>roles list</em> anyway and run it through the real <see cref="KgsmRoleMap"/>,
/// because the cases that matter turn on the difference between <see langword="null"/> (not a member),
/// an empty list (a member holding nothing, floored at viewer) and an elevated role. Returning a tier
/// directly would assert what the stub was told to say and lose that distinction entirely.
/// </para>
/// <para>
/// One map serves the whole project: every host these tests build configures <c>role-admin</c> as
/// admin and <c>role-op</c>/<c>role-123</c> as operator, and no id means one thing in one test and
/// something else in another. If that ever stops being true, the map moves back into each test.
/// </para>
/// </remarks>
internal static class AuthStubs
{
    private static readonly KgsmRoleMap RoleMap = new KgsmAuthOptions
    {
        RoleAdminIds = "role-admin",
        RoleOperatorIds = "role-op,role-123",
    }.ToRoleMap();

    /// <summary>The tier the configured role map grants for <paramref name="roles"/>.</summary>
    public static KgsmTier TierFor(IReadOnlyList<string>? roles) => RoleMap.Resolve(roles);

    /// <summary>
    /// Stub the authority as the provider would answer it. The seam is substituted for both halves of
    /// the sign-in, so the cast reaches the same object the handler resolves.
    /// </summary>
    public static void StubTier(ISignInService seam, IReadOnlyList<string>? roles) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns(_ => RoleMap.Resolve(roles));

    /// <summary>
    /// Stub the authority as unreachable. This is an outage, and every caller must keep reporting it as
    /// one — a denial would tell an operator they lost a role they still hold.
    /// </summary>
    public static void StubTierUnreachable(ISignInService seam) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns<KgsmTier>(_ => throw new DiscordAuthException("Discord unreachable."));
}
