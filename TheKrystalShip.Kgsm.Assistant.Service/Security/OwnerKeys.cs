using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Resolves the account a caller is, which is what everything of theirs is stored under.
/// </summary>
/// <remarks>
/// <para>
/// One person has as many subjects as they have ways of signing in — a Discord id when they came
/// through Discord, an account id when they came through a password, and a different one again on
/// whichever door is added next. Keying storage on the subject therefore gives one person a separate
/// history per door, silently, and the day a cluster moves everybody to one sign-in it moves them all
/// into namespaces they have never used.
/// </para>
/// <para>
/// So storage keys on the <b>account</b>, which is the person themselves and does not change. The
/// subject stays what it always was: what proves them, and what an authority question is asked about.
/// </para>
/// <para>
/// A caller the store does not know keeps their subject as the key. That is the key they have always
/// had, so an unknown caller is unchanged rather than homeless — and it is why the fallback is the
/// subject rather than something newly invented.
/// </para>
/// </remarks>
internal static class OwnerKeys
{
    /// <summary>
    /// The account this provider/subject pair belongs to, or <see langword="null"/> when the store does
    /// not know them or cannot be read.
    /// </summary>
    /// <remarks>
    /// An unreadable store answers the same as an unknown caller. Both mean "no account to key on", and
    /// failing the request instead would take conversations offline over a locked file.
    /// </remarks>
    public static async Task<string?> ResolveAsync(
        UserDirectory users, string provider, string subject, CancellationToken ct)
    {
        if (!users.Available || string.IsNullOrEmpty(subject))
            return null;

        try
        {
            return (await users.Store.FindByCredentialAsync(KgsmActor.Format(provider, subject), ct))?.UserId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
