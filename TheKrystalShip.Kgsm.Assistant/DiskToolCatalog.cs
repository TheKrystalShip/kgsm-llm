using Microsoft.Extensions.Configuration;

using TheKrystalShip.Agent;
using TheKrystalShip.Agent.Tools;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <inheritdoc />
/// <remarks>
/// Reads <c>tools.json</c> from <see cref="FilePromptOverrides.DirectoryKey"/> once, at construction,
/// through <see cref="ToolCatalogFile"/>: the file's shape, its validation and the agreement check are
/// that type's. What is this assistant's own is the binding. Every entry names the capability it
/// implements, and the tiers in <see cref="LlmTools"/> decide who is offered it.
/// </remarks>
public sealed class DiskToolCatalog : IToolCatalog
{
    public const string FileName = ToolCatalogFile.FileName;

    public IReadOnlyList<LlmToolDefinition> ReadOnly { get; }
    public IReadOnlyList<LlmToolDefinition> All { get; }
    public LlmToolDefinition ReviseBlueprintTool { get; }

    public DiskToolCatalog(IConfiguration configuration)
        : this(configuration[FilePromptOverrides.DirectoryKey]) { }

    public DiskToolCatalog(string? directory)
    {
        var entries = ToolCatalogFile.Read(directory);
        var path = Path.Combine(directory!, FileName);

        // Names come from the file, capabilities from the code, and this is where they are bound. The
        // pairing must be exactly one-to-one: an entry naming no capability binds to nothing, and two
        // entries claiming one capability would leave which name the tool has to the order of the file.
        var byCapability = new Dictionary<Capability, ToolEntry>();
        foreach (var entry in entries)
        {
            if (entry.Capability is null)
                throw new AssistantTextUnavailableException(
                    $"{path}: tool '{entry.Name}' declares no capability. Every entry names the capability it " +
                    "implements — that is what binds it to a handler, and a name alone binds to nothing.");

            var capability = new Capability(entry.Capability);
            if (byCapability.TryGetValue(capability, out var already))
                throw new AssistantTextUnavailableException(
                    $"{path}: '{entry.Name}' and '{already.Name}' both declare the capability '{capability}'. " +
                    "One capability is one tool; which name it takes cannot depend on the order of the file.");

            byCapability[capability] = entry;
        }

        ToolCatalogFile.RequireAgreement(
            path, byCapability.Keys.Select(c => c.Id), LlmTools.EveryCapability.Select(c => c.Id), "capability");

        _names = byCapability.ToDictionary(kv => kv.Key, kv => new Tool(kv.Value.Name));
        _capabilities = byCapability.ToDictionary(kv => kv.Value.Name, kv => kv.Key, StringComparer.Ordinal);
        _labels = byCapability.Values.ToDictionary(e => e.Name, e => e.Label, StringComparer.Ordinal);

        LlmToolDefinition Define(Capability capability) => byCapability[capability].Definition;

        StagedCommandTools = LlmTools.StagedCommandsTier.Select(c => _names[c]).ToHashSet();
        AuthorizedReadTools = LlmTools.AuthorizedReadOnlyTier.Select(c => _names[c]).ToHashSet();

        // The personal tier joins BOTH offers: it is open to everyone, so it belongs in the
        // unauthorized set as well as the full one. Present in only one of them, a caller with no
        // authority over any server would silently lose the ability to remember their own preferences.
        ReadOnly = LlmTools.ReadOnlyTier.Concat(LlmTools.PersonalTier).Select(Define).ToArray();
        All = LlmTools.ReadOnlyTier
            .Concat(LlmTools.PersonalTier)
            .Concat(LlmTools.AuthorizedReadOnlyTier)
            .Concat(LlmTools.StagedCommandsTier)
            .Concat(LlmTools.AuthorizedActionsTier)
            .Where(c => c != LlmTools.ReviseBlueprint)
            .Select(Define)
            .ToArray();
        ReviseBlueprintTool = Define(LlmTools.ReviseBlueprint);
    }

    private readonly IReadOnlyDictionary<Capability, Tool> _names;
    private readonly IReadOnlyDictionary<string, Capability> _capabilities;
    private readonly IReadOnlyDictionary<string, string?> _labels;

    /// <inheritdoc />
    public Tool NameOf(Capability capability) => _names[capability];

    /// <inheritdoc />
    public Capability? CapabilityOf(Tool tool) =>
        _capabilities.TryGetValue(tool.Name, out var c) ? c : null;

    /// <inheritdoc />
    public string? LabelOf(Tool tool) => _labels.GetValueOrDefault(tool.Name);

    /// <inheritdoc />
    public IReadOnlySet<Tool> StagedCommandTools { get; }

    /// <inheritdoc />
    public IReadOnlySet<Tool> AuthorizedReadTools { get; }
}
