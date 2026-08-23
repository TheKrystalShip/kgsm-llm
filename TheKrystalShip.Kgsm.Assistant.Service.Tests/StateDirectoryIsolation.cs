using System.Runtime.CompilerServices;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Keeps this test assembly out of the host's real state directory.
/// </summary>
/// <remarks>
/// <para>
/// These tests boot the real <c>Program</c>, so a test host inherits this machine's paths.
/// <c>StatePaths.Directory</c> falls back to <c>/var/lib/kgsm-assistant</c> when there is no
/// <c>$STATE_DIRECTORY</c>, and that is the live service's own directory: a test host with no
/// <c>Conversation:DatabasePath</c> of its own opens the running assistant's conversation database,
/// and one with no <c>Auth:SigningKey</c> writes a signing key into the file the running assistant
/// would read.
/// </para>
/// <para>
/// A module initializer for the same reason <see cref="JournalIsolation"/> is one: a dozen classes
/// take a bare <c>WebApplicationFactory&lt;Program&gt;</c> with no shared base to hang this off, and
/// a thirteenth added later would reach the live directory again. <c>$STATE_DIRECTORY</c> is
/// process-global, which is exactly the scope wanted.
/// </para>
/// <para>
/// <c>StatePathsTests</c> sets and clears the same variable deliberately; it runs unparallelised and
/// restores whatever it found, which is this.
/// </para>
/// </remarks>
internal static class StateDirectoryIsolation
{
    /// <summary>Where this assembly's state goes instead.</summary>
    internal static string Directory { get; } = Path.Combine(
        Path.GetTempPath(), "kgsm-assistant-tests-state", Path.GetRandomFileName());

    [ModuleInitializer]
    internal static void Redirect()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", Directory);
    }
}
