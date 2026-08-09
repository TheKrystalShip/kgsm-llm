using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// This host's KGSM accounts, and whether they can be reached at all.
/// </summary>
/// <remarks>
/// <para>
/// Opened straight off the shared store file, not asked for over HTTP. That is what keeps this leaf
/// standalone: a file cannot be down, so the assistant signs someone in with kgsm-api absent, and no
/// leaf ends up authenticating through a sibling.
/// </para>
/// <para>
/// <b>Opening it can fail, and that must not stop the service.</b> A permission problem, or a store
/// written by a newer build sharing this host, leaves password sign-in unavailable while Discord
/// sign-in and every conversation keep working — so the failure is captured here and reported, the
/// same shape as any other capability. Refusing to start would let another service's deploy order
/// decide whether the assistant exists.
/// </para>
/// </remarks>
internal sealed class UserDirectory
{
    private readonly SqliteUserStore? _store;
    private readonly LocalSignInService? _signIn;

    public UserDirectory(IOptions<AuthOptions> options, ILogger<UserDirectory> logger)
    {
        string path = options.Value.UsersDbPath;

        try
        {
            _store = new SqliteUserStore(new UserStoreOptions { Path = path });
            Authority = new UserStoreAuthority(_store);
            _signIn = new LocalSignInService(_store, new IdentityPasswordHasher(), Authority);
            logger.LogInformation("KGSM account store opened at {Path}.", path);
        }
        catch (UserStoreSchemaException e)
        {
            // The loud case: another KGSM service on this host wrote a schema this build does not
            // understand, so reading it would mean guessing at accounts.
            UnavailableReason = e.Message;
            logger.LogError(e,
                "KGSM account store at {Path} is a schema this build does not understand. Password " +
                "sign-in is unavailable until this service is brought up to the same version as the " +
                "rest of the host.", path);
        }
        catch (Exception e)
        {
            UnavailableReason = $"The KGSM account store at '{path}' could not be opened.";
            logger.LogError(e,
                "KGSM account store at {Path} could not be opened. Password sign-in is unavailable; " +
                "sign-in through an identity provider is unaffected.", path);
        }
    }

    /// <summary>Whether accounts can be read at all.</summary>
    public bool Available => _store is not null;

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>.</summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// Authority from the account store. Answers <see cref="KgsmTier.None"/> when unavailable —
    /// deliberately, because a caller here is asking about an account it has no way to look up, which
    /// is indistinguishable from one that does not exist.
    /// </summary>
    public UserStoreAuthority? Authority { get; }

    /// <summary>Username-and-password sign-in. Only valid while <see cref="Available"/>.</summary>
    public LocalSignInService SignIn =>
        _signIn ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// The accounts themselves. Only valid while <see cref="Available"/>.
    /// </summary>
    /// <remarks>
    /// This surface reads accounts and never writes them: creating, approving and retiering happen in
    /// the Control Panel, which is the one place authority on this host is decided. Exposed so the
    /// tests can stand a real account up, and so a later read (a display name, a status) has the store
    /// to ask rather than a second copy to drift from.
    /// </remarks>
    public IUserStore Store =>
        _store ?? throw new InvalidOperationException("The KGSM account store is unavailable.");
}

/// <summary>
/// Authority, routed by which provider verified the caller: a KGSM account answers from the account
/// store, anything else from the identity provider that vouched for it.
/// </summary>
/// <remarks>
/// <para>
/// This service re-derives authority on every request rather than reading it off the bearer, so a
/// password sign-in has to be answerable per request too — and the account store is the only thing
/// that can answer for one. Sending a <c>local:</c> handle to Discord would ask about a guild member
/// who does not exist and get a denial for somebody who is signed in perfectly legitimately.
/// </para>
/// <para>
/// Routing here rather than teaching either provider about the other is the point of
/// <see cref="IAuthorityProvider"/> being a seam: each half still answers only for what it knows, and
/// an outage on either side still throws rather than resolving to a denial.
/// </para>
/// </remarks>
internal sealed class RoutedAuthority(UserDirectory users, IAuthorityProvider fallback) : IAuthorityProvider
{
    public Task<KgsmTier> ResolveTierAsync(KgsmIdentity identity, CancellationToken ct)
    {
        if (identity.Provider != KgsmActorProvider.Local)
            return fallback.ResolveTierAsync(identity, ct);

        return users.Authority is { } authority
            ? authority.ResolveTierAsync(identity, ct)
            // A KGSM account with no store to read is a question that cannot be answered, and this
            // seam's contract says an unanswerable question throws rather than denying — the caller
            // reports an outage, and nobody is told they lost authority they still hold.
            : throw new KgsmAuthProviderException(
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");
    }
}
