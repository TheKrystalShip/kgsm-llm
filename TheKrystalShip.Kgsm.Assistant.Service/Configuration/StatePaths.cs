namespace TheKrystalShip.Kgsm.Assistant.Service.Configuration;

/// <summary>
/// Where the service's persistent state lives — the conversation database, which also carries the
/// staged actions and the session registry, and the RAG index the indexer writes beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, in order.</b> A configured path that differs from the shipped default is an
/// operator naming a location deliberately, and wins. Otherwise the file sits in
/// <c>$STATE_DIRECTORY</c>, which systemd creates before <c>ExecStart</c> and chowns to the unit's
/// <c>User=</c> from <c>StateDirectory=kgsm-assistant</c> — so the directory costs no privilege to
/// provision and follows the user the unit is templated with, whether or not that account has a home
/// directory. Both units declare it, which is how the indexer writes the index the service reads.
/// </para>
/// <para>
/// Outside systemd — a test, or the service started from a terminal — there is no
/// <c>$STATE_DIRECTORY</c> and the shipped default is used as it stands. Both name
/// <c>/var/lib/kgsm-assistant</c> on a deployed host, so the database is the same file either way.
/// The CLI is a different program with no state directory of its own: it leaves
/// <c>Conversation:DatabasePath</c> blank and keeps its history beside its binary.
/// </para>
/// </remarks>
internal static class StatePaths
{
    /// <summary>
    /// The directory the units' <c>StateDirectory=kgsm-assistant</c> resolves to, and the location
    /// every shipped default names.
    /// </summary>
    public const string DefaultDirectory = "/var/lib/kgsm-assistant";

    /// <summary>The conversation database's location when nobody has configured one.</summary>
    public const string DefaultConversationDbPath = DefaultDirectory + "/conversations.db";

    /// <summary>The conversation database's file name inside <see cref="Directory"/>.</summary>
    public const string ConversationDbFileName = "conversations.db";

    /// <summary>
    /// The file this host keeps the key it signs sessions with, used only when
    /// <c>Auth:SigningKey</c> is blank.
    /// </summary>
    /// <remarks>
    /// Resolved against <see cref="Directory"/> rather than <see cref="Resolve"/>, because it has no
    /// configured path to defer to: an operator who wants to name the key names the key itself.
    /// </remarks>
    public static string SigningKeyPath => Path.Combine(Directory, "signing-key");

    /// <summary>
    /// The directory systemd provisioned for this unit, or <see cref="DefaultDirectory"/> when the
    /// process is not running under one.
    /// </summary>
    /// <remarks>
    /// <c>$STATE_DIRECTORY</c> is colon-separated when a unit declares several directories; these
    /// units declare a single directory, and the first entry is the assistant's either way.
    /// </remarks>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("STATE_DIRECTORY") is not { Length: > 0 } value)
                return DefaultDirectory;

            string[] parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : DefaultDirectory;
        }
    }

    /// <summary>
    /// The path a state file is actually opened at: the shipped default is redirected into
    /// <see cref="Directory"/>, and every other value is left exactly as it was given.
    /// </summary>
    /// <remarks>
    /// Blank is left alone rather than redirected, because blank is already an answer everywhere it
    /// can be set — <c>Conversation:DatabasePath</c> blank keeps the history beside the binary (which
    /// is what the CLI wants), and <c>Rag:IndexPath</c> blank is the retrieval off switch.
    /// </remarks>
    /// <param name="configured">The configured value, e.g. <c>Conversation:DatabasePath</c>.</param>
    /// <param name="shippedDefault">The default that value carries when nobody has set it.</param>
    /// <param name="fileName">The file's name inside <see cref="Directory"/>.</param>
    public static string? Resolve(string? configured, string shippedDefault, string fileName) =>
        string.Equals(configured, shippedDefault, StringComparison.Ordinal)
            ? Path.Combine(Directory, fileName)
            : configured;
}
