namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The repo's own <c>deploy/prompts/</c> — the text that actually ships. Tests build their catalog
/// from it rather than from a fixture, so the shipped <c>tools.json</c> is validated against the
/// dispatcher on every test run: a tool renamed in code without the file being updated (or the other
/// way round) fails the suite here rather than the service at boot.
/// </summary>
internal static class ShippedText
{
    public static string Directory { get; } = Locate();

    public static IToolCatalog Catalog { get; } = new DiskToolCatalog(Directory);

    /// <summary>The shipped text of one segment, trimmed exactly as the reader trims it.</summary>
    public static string Segment(string fileName) =>
        File.ReadAllText(Path.Combine(Directory, fileName)).Trim();

    /// <summary>Copies the shipped text into <paramref name="target"/> — the state a deploy leaves behind.</summary>
    public static void SeedInto(string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.GetFiles(Directory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

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
