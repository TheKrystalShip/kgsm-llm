using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Agent.Prompts;

namespace TheKrystalShip.Kgsm.Assistant;

/// <inheritdoc />
/// <remarks>
/// Reads from the directory at config key <see cref="DirectoryKey"/> through a
/// <see cref="PromptDirectory"/>, whose variants are this assistant's leaves: a calling leaf's own
/// file answers first and the host-wide file answers for everything it does not override. Each read
/// hits the filesystem, so an edit applies to the next turn. No directory configured ⇒ every read is
/// absent.
/// </remarks>
public sealed class FilePromptOverrides : IPromptOverrides
{
    public const string DirectoryKey = "Prompts:Directory";

    private readonly PromptDirectory _directory;

    public FilePromptOverrides(IConfiguration configuration, ILogger<FilePromptOverrides> logger) =>
        _directory = new PromptDirectory(configuration[DirectoryKey], logger);

    public string? ReadText(string fileName, string? leaf = null) => _directory.ReadText(fileName, leaf);
}
