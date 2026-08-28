using System.Runtime.CompilerServices;

using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Keeps this test assembly out of the host's real journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not theorised.</b> These tests boot the real <c>Program</c> through
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>, which is deliberately
/// fake-free — so the host they start inherits this machine's paths, and the service now writes a
/// <c>leaf_ready</c> the moment it starts. One full run put <b>474 ready/stopping pairs</b> into
/// <c>/var/lib/kgsm-assistant/events</c>: a record of a leaf starting and stopping five hundred times
/// on a host where it did no such thing, in the file the incident tools read back.
/// </para>
/// <para>
/// The same failure happened in kgsm-api, for the same reason, and was found the same way — by looking
/// at the live file rather than by any test noticing.
/// </para>
/// <para>
/// A module initializer rather than a fixture, because it has to hold for <b>every</b> test host in
/// this assembly including ones nobody has written yet: a dozen classes take a bare
/// <c>WebApplicationFactory&lt;Program&gt;</c> with no shared base to hang this off, and a thirteenth
/// added later would silently write to the real journal again. The environment variable is
/// process-global, which is exactly the scope wanted — nothing in this process may write there.
/// </para>
/// </remarks>
internal static class JournalIsolation
{
    /// <summary>Where this assembly's journals go instead.</summary>
    internal static string StateRoot { get; } = Path.Combine(
        Path.GetTempPath(), "kgsm-assistant-tests", Path.GetRandomFileName());

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory.CreateDirectory(StateRoot);
        Environment.SetEnvironmentVariable(
            JournalServiceCollectionExtensions.StateRootVariable, StateRoot);
    }
}
