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
    private readonly IdentityLinkService? _linking;

    public UserDirectory(IOptions<AuthOptions> options, ILogger<UserDirectory> logger)
    {
        string path = options.Value.UsersDbPath;

        Pending = new PendingPolicy(options.Value.PendingUserCap, TimeSpan.FromDays(options.Value.PendingUserTtlDays));

        try
        {
            _store = new SqliteUserStore(new UserStoreOptions { Path = path });
            // Uncached deliberately. The read behind it is a local point query, and this surface
            // already caches the tier it derives from it for the role-cache TTL — a second cache under
            // the first would add staleness and buy microseconds.
            Authority = new UserStoreAuthority(_store);
            _linking = new IdentityLinkService(_store);
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
    /// Authority from the account store — this host's only answer to what a verified caller may do.
    /// <see langword="null"/> while <see cref="Available"/> is false.
    /// </summary>
    public UserStoreAuthority? Authority { get; }

    /// <summary>
    /// Turning a verified external identity into the account it proves. Only valid while
    /// <see cref="Available"/>.
    /// </summary>
    public IdentityLinkService Linking =>
        _linking ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>How many unapproved accounts this host will hold, and for how long.</summary>
    public PendingPolicy Pending { get; }

    /// <summary>Username-and-password sign-in. Only valid while <see cref="Available"/>.</summary>
    public LocalSignInService SignIn =>
        _signIn ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// The accounts themselves. Only valid while <see cref="Available"/>.
    /// </summary>
    /// <remarks>
    /// This surface never decides what anyone may do: creating, approving and retiering happen in the
    /// Control Panel, which is the one place authority on this host is set. The one thing it writes is
    /// an unapproved account for an identity nobody has claimed yet, so that somebody signing in here
    /// for the first time appears on that screen to be approved.
    /// </remarks>
    public IUserStore Store =>
        _store ?? throw new InvalidOperationException("The KGSM account store is unavailable.");
}

/// <summary>
/// Authority: the account store, for everyone, however they proved who they are.
/// </summary>
/// <remarks>
/// <para>
/// A provider says who someone is and contributes nothing else. A guild role is a fact about a chat
/// server, not about this host — so a Discord login and a password login are answered from the same
/// record, and a person holds the same tier here as they do in the Control Panel because both read
/// that record rather than each deriving one.
/// </para>
/// <para>
/// This service re-derives authority on every request rather than reading it off the bearer, so a
/// change made in the panel takes effect without anyone signing in again.
/// </para>
/// <para>
/// A store that cannot be read <b>throws</b>. This seam's contract is that an unanswerable question
/// is never a denial: the caller reports an outage, and nobody is told they lost authority they
/// still hold.
/// </para>
/// </remarks>
internal sealed class AccountAuthority(UserDirectory users) : IAuthorityProvider
{
    public Task<KgsmTier> ResolveTierAsync(KgsmIdentity identity, CancellationToken ct) =>
        users.Authority is { } authority
            ? authority.ResolveTierAsync(identity, ct)
            : throw new KgsmAuthProviderException(
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");
}
