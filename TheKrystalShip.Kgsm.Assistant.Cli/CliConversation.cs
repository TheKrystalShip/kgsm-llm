namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// The ids the CLI opens conversations under: <c>cli:{osuser}:{run}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same three-part shape the web surface uses (<c>web:{userId}:{chatId}</c>), and for the same
/// reason. An owner is read off a conversation id as everything up to its second <c>:</c>
/// (<c>MemoryScope</c>), so the OS user has to be its own segment: as a flat <c>cli:{run}</c> every
/// invocation was its own owner, and anything the assistant wrote down was unreachable from the very
/// next command.
/// </para>
/// <para>
/// The run segment still changes per invocation, so one-shot runs stay separate conversations that
/// do not replay each other's transcripts — it is the memory that crosses them, which is the whole
/// distinction between the two stores.
/// </para>
/// </remarks>
internal static class CliConversation
{
    /// <summary>A fresh conversation for this run, owned by the OS user running it.</summary>
    public static string NewId() => $"cli:{Owner()}:{Guid.NewGuid():N}";

    /// <summary>
    /// The OS user as a conversation-id segment, reduced to <c>[A-Za-z0-9_-]</c> so a name carrying a
    /// <c>:</c> cannot invent a segment boundary and land in somebody else's namespace. An empty
    /// result falls back to a constant rather than to nothing, which would collapse the segment and
    /// make every user one owner.
    /// </summary>
    private static string Owner()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
            return "local";

        var safe = new string([.. user.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')]);
        return safe.Length == 0 ? "local" : safe;
    }
}
