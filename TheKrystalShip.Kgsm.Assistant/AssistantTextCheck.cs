using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Proves, at startup, that the assistant's on-disk text is complete and usable: every prompt segment
/// resolves to something non-empty, and <c>tools.json</c> describes exactly the tools the dispatcher
/// can run.
/// <para>
/// A host calls this before serving. The alternative is discovering a missing file on the first
/// question somebody asks, which is the worst possible moment and — for a prompt segment — may not
/// look like a fault at all, just an assistant behaving differently than it used to.
/// </para>
/// </summary>
public static class AssistantTextCheck
{
    /// <summary>
    /// Throws <see cref="AssistantTextUnavailableException"/> with a message naming the file and what
    /// to do about it. Returns the directory the text was read from, for logging.
    /// </summary>
    public static string Validate(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var overrides = services.GetRequiredService<IPromptOverrides>();

        var directory = configuration[FilePromptOverrides.DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
            throw new AssistantTextUnavailableException(
                $"'{FilePromptOverrides.DirectoryKey}' is not set. The assistant's prompts and tool " +
                "definitions live on disk; point it at the directory they were installed into.");

        var missing = PromptSegments.All
            .Where(s => overrides.ReadText(s.FileName) is null
                        && string.IsNullOrWhiteSpace(configuration[s.ConfigKey]))
            .Select(s => s.FileName)
            .ToList();

        if (missing.Count > 0)
            throw new AssistantTextUnavailableException(
                $"{directory} is missing {missing.Count} prompt segment(s): {string.Join(", ", missing)}. " +
                "The assistant's prompts live on disk — run deploy/deploy.sh to install them.");

        // Constructing it IS the validation: it refuses a catalog that disagrees with the dispatcher.
        _ = services.GetRequiredService<IToolCatalog>();

        return directory;
    }
}
