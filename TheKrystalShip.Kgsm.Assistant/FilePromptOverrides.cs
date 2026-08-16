using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.Kgsm.Assistant;

/// <inheritdoc />
/// <remarks>
/// Reads from the directory at config key <see cref="DirectoryKey"/>. Each read hits the filesystem
/// (the files are tiny and a turn is human-paced), so edits are picked up on the next turn without a
/// <see cref="System.IO.FileSystemWatcher"/> — which double-fires and trips over editors' atomic-save
/// renames. No directory configured ⇒ every method is inert (returns the in-code default).
/// </remarks>
public sealed class FilePromptOverrides : IPromptOverrides
{
    public const string DirectoryKey = "Prompts:Directory";

    private readonly string? _directory;
    private readonly ILogger<FilePromptOverrides> _logger;

    public FilePromptOverrides(IConfiguration configuration, ILogger<FilePromptOverrides> logger)
    {
        var dir = configuration[DirectoryKey];
        _directory = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _logger = logger;
    }

    public string? ReadText(string fileName, string? leaf = null)
    {
        if (_directory is null)
            return null;

        // The calling leaf's own text, then the host-wide text. Falling through rather than
        // requiring a leaf to restate every segment is what lets a surface override only the one
        // line that differs for it and inherit the rest.
        foreach (var path in CandidatePaths(fileName, leaf))
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var text = File.ReadAllText(path).Trim();
                // Blank counts as absent — a mid-save truncation falls back to the default for one turn
                // rather than blanking the prompt. A blank leaf file falls through to the host-wide one
                // for the same reason, so a half-saved override never blanks a surface either.
                if (text.Length > 0)
                    return text;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read prompt override {File}; using default", path);
            }
        }

        return null;
    }

    /// <summary>
    /// The files that may answer for a segment, most specific first: the leaf's own, then the
    /// host-wide one. An unusable leaf name yields only the host-wide path — an unrecognised leaf
    /// reads the assistant's own text rather than nothing at all.
    /// </summary>
    private IEnumerable<string> CandidatePaths(string fileName, string? leaf)
    {
        if (LeafName.Validate(leaf) is { } validLeaf)
            yield return Path.Combine(_directory!, validLeaf, fileName);

        yield return Path.Combine(_directory!, fileName);
    }
}
