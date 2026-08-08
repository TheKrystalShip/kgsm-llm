using System.Text.Json;
using System.Text.Json.Serialization;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// The command catalog the Control Panel lists for this leaf: what a person can type at the
/// assistant, in the assistant's own words.
/// <para>
/// It is a <strong>file this repo's deploy ships</strong>, for the same reason the leaf config
/// descriptor is one — the panel reads it by scanning a directory, so a leaf that grows a command
/// surface becomes documented by landing one file, with no rebuild in <c>kgsm-api</c>. It is
/// <strong>generated from <see cref="ChatCommands"/></strong>, the same catalog
/// <c>GET /commands</c> serves and the CLI dispatches from, so the panel and the composer cannot
/// disagree about what exists.
/// </para>
/// <para>
/// The one difference from the live endpoint: this file is the <b>whole</b> catalog, where the
/// endpoint is filtered to the caller's tier. A live surface shows a person what they can type; a
/// descriptive file documents the leaf.
/// </para>
/// </summary>
internal sealed record CommandManifest(
    int SchemaVersion,
    string Leaf,
    string Surface,
    IReadOnlyDictionary<string, IReadOnlyList<ManifestCommand>> Gates)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Read the catalog into the shipped shape: commands keyed by the gate that admits them.</summary>
    public static CommandManifest Build() => new(
        SchemaVersion: ChatCommands.SchemaVersion,
        Leaf: ChatCommands.LeafId,
        Surface: ChatCommands.Surface,
        Gates: ChatCommands.All
            .GroupBy(c => c.Gate)
            // Strongest gate last, so the file reads from what anyone can type down to what only an
            // admin can. Ordinal within a bucket keeps a committed file from churning on an unrelated
            // build — the catalog's own order is source order, which a reordering edit would change.
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => KgsmTiers.ToWire(g.Key),
                g => (IReadOnlyList<ManifestCommand>)[.. g
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(ManifestCommand.From)],
                StringComparer.Ordinal));

    /// <summary>Generate the manifest and write it where the deploy expects to find it.</summary>
    public static void WriteTo(string path)
    {
        var json = JsonSerializer.Serialize(Build(), JsonOptions);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json + Environment.NewLine);
    }
}

/// <summary>One command in the shipped manifest. The gate is the bucket it sits in, not a field.</summary>
internal sealed record ManifestCommand(
    string Name,
    string Description,
    bool Mutates,
    IReadOnlyList<ManifestOption> Options)
{
    public static ManifestCommand From(ChatCommand command) => new(
        command.Name,
        command.Description,
        command.Mutates,
        [.. command.Options.Select(ManifestOption.From)]);
}

/// <summary>One option of a manifest command. <c>Values</c> is absent when the option takes free text.</summary>
internal sealed record ManifestOption(
    string Name,
    string? Description,
    string Type,
    bool Required,
    bool Autocomplete,
    IReadOnlyList<string>? Values)
{
    public static ManifestOption From(ChatCommandOption option) => new(
        option.Name, option.Description, option.Type, option.Required, option.Autocomplete, option.Values);
}
