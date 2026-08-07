using System.Text.Encodings.Web;
using System.Text.Json;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Materializes the DEFAULT prompt segments and a <c>tools.json</c> template into a directory so an
/// operator has editable starting points (the precedence layer then makes those files override the
/// constants). Seeds from the in-code defaults rather than shipping duplicate files — keeping the
/// <see cref="KgsmAssistantPrompts"/> constants and <see cref="LlmTools"/> the single source of truth.
/// Never clobbers an existing file (so re-running only fills gaps, never overwrites edits).
/// </summary>
public static class PromptScaffold
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record DumpResult(IReadOnlyList<string> Written, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Writes <c>preamble.md</c> / <c>actions-allowed.md</c> / <c>actions-denied.md</c> / <c>tools.json</c>
    /// into <paramref name="directory"/>, skipping any that already exist. Returns which were written
    /// vs already present.
    /// </summary>
    /// <param name="leaf">
    /// Seed a calling leaf's own overrides at <c>&lt;directory&gt;/&lt;leaf&gt;/</c> instead of the
    /// assistant's own at the root. Seeded from the same in-code defaults, so an operator starts from
    /// working text and edits only the lines that differ for that surface — an unedited copy behaves
    /// identically to having no override at all. An unusable leaf name is refused rather than written
    /// somewhere unexpected.
    /// </param>
    public static DumpResult WriteDefaults(string directory, string? leaf = null)
    {
        if (leaf is not null)
        {
            var validLeaf = LeafName.Validate(leaf)
                ?? throw new ArgumentException($"'{leaf}' is not a usable leaf name.", nameof(leaf));
            directory = Path.Combine(directory, validLeaf);
        }

        Directory.CreateDirectory(directory);

        var written = new List<string>();
        var skipped = new List<string>();

        foreach (var segment in PromptSegments.All)
            WriteIfAbsent(Path.Combine(directory, segment.FileName), segment.Default + "\n", written, skipped);

        WriteIfAbsent(Path.Combine(directory, FilePromptOverrides.ToolsFileName), ToolsTemplate(), written, skipped);

        return new DumpResult(written, skipped);
    }

    private static void WriteIfAbsent(string path, string content, List<string> written, List<string> skipped)
    {
        if (File.Exists(path))
        {
            skipped.Add(path);
            return;
        }

        File.WriteAllText(path, content);
        written.Add(path);
    }

    /// <summary>A tools.json seeded from the current default tool/param descriptions (names + prose).</summary>
    private static string ToolsTemplate()
    {
        var map = LlmTools.All.ToDictionary(
            tool => tool.Name,
            tool => new ToolTemplate(
                tool.Description,
                tool.Parameters.ToDictionary(p => p.Name, p => p.Description)));

        return JsonSerializer.Serialize(map, Json) + "\n";
    }

    // Lower-cased property names match the override reader (FilePromptOverrides.ToolTextOverride).
    private sealed record ToolTemplate(string description, IReadOnlyDictionary<string, string> @params);
}
