namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The repo's own <c>deploy/prompts/</c>. An adapter's refusal names the tool the model should reach
/// for next, and the name comes from the catalog — so a test asserting that text needs the catalog
/// that actually ships, not a fixture that can drift from it.
/// </summary>
internal static class ShippedPrompts
{
    public static string Directory { get; } = Locate();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "prompts");
            if (File.Exists(Path.Combine(candidate, DiskToolCatalog.FileName)))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find deploy/prompts above " + AppContext.BaseDirectory);
    }
}
