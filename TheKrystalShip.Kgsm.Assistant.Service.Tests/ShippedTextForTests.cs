using System.Runtime.CompilerServices;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Points every test host at the repo's own <c>deploy/prompts/</c>.
/// <para>
/// The service reads its prompts and tool definitions from disk and refuses to start without them,
/// so a test host needs the same files a deployed one gets. Setting it here — once, before any host
/// is built — keeps it out of the nine test classes that construct a
/// <c>WebApplicationFactory&lt;Program&gt;</c> for their own reasons, and means a test runs against
/// the text that actually ships rather than a fixture that can drift from it.
/// </para>
/// </summary>
internal static class ShippedTextForTests
{
    /// <summary>
    /// The shipped catalog, for tests that need to know what a capability is CALLED — a frame's tool
    /// name, a recorded trajectory. Built from the same directory the hosts are pointed at, so a
    /// rename in <c>tools.json</c> moves the assertion with it.
    /// </summary>
    internal static IToolCatalog Catalog { get; } =
        new DiskToolCatalog(Environment.GetEnvironmentVariable("Prompts__Directory"));

    internal static Llm.Models.Tool Name(Capability capability) => Catalog.NameOf(capability);

    [ModuleInitializer]
    internal static void PointAtShippedPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "prompts");
            if (File.Exists(Path.Combine(candidate, "tools.json")))
            {
                Environment.SetEnvironmentVariable("Prompts__Directory", candidate);
                return;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find deploy/prompts above " + AppContext.BaseDirectory);
    }
}
