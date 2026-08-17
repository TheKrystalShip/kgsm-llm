using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Thrown when the on-disk assistant text cannot be used. It is raised from the constructor of the
/// thing that needs it, so a host resolving that service fails to start rather than answering with
/// half a catalog — an assistant offering a tool the dispatcher cannot run, or missing the tool the
/// prompt tells the model to use, is worse than one that does not come up.
/// </summary>
public sealed class AssistantTextUnavailableException(string message) : Exception(message);

/// <inheritdoc />
/// <remarks>
/// Reads <c>tools.json</c> from <see cref="FilePromptOverrides.DirectoryKey"/> ONCE, at construction.
/// Unlike the prompt segments — which are re-read every turn because a bad edit costs one turn — the
/// catalog is the contract between the model and the dispatcher, and swapping it under a turn in
/// flight would let a tool be offered and then not exist when it is called. Editing it takes a
/// restart, and the restart is what validates it.
/// </remarks>
public sealed class DiskToolCatalog : IToolCatalog
{
    public const string FileName = "tools.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly IReadOnlySet<string> KnownTypes =
        new HashSet<string>(StringComparer.Ordinal) { "string", "integer", "number", "boolean" };

    public IReadOnlyList<LlmToolDefinition> ReadOnly { get; }
    public IReadOnlyList<LlmToolDefinition> All { get; }
    public LlmToolDefinition ReviseBlueprintTool { get; }

    public DiskToolCatalog(IConfiguration configuration)
        : this(configuration[FilePromptOverrides.DirectoryKey]) { }

    public DiskToolCatalog(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new AssistantTextUnavailableException(
                $"'{FilePromptOverrides.DirectoryKey}' is not set. The assistant's prompts and tool " +
                "definitions live on disk; point it at the directory they were installed into.");

        var path = Path.Combine(directory, FileName);
        var byName = Parse(path);

        // Names come from the file, capabilities from the code, and this is where they are bound. A
        // tool declares which capability it implements; the pairing must be exactly one-to-one, and
        // each way it can fail loses something different:
        //   · a capability with no entry — the model silently loses a tool the assistant can run
        //   · an entry naming no capability, or one nothing implements — the model is offered a tool
        //     that fails the turn it is called on
        //   · two entries claiming one capability — which name the tool has becomes file order
        var byCapability = new Dictionary<Capability, string>();
        foreach (var (name, doc) in byName)
        {
            if (string.IsNullOrWhiteSpace(doc.Capability))
                throw new AssistantTextUnavailableException(
                    $"{path}: tool '{name}' declares no capability. Every entry names the capability it " +
                    "implements — that is what binds it to a handler, and a name alone binds to nothing.");

            var capability = new Capability(doc.Capability.Trim());
            if (byCapability.TryGetValue(capability, out var already))
                throw new AssistantTextUnavailableException(
                    $"{path}: '{name}' and '{already}' both declare the capability '{capability}'. One " +
                    "capability is one tool; which name it takes cannot depend on the order of the file.");

            byCapability[capability] = name;
        }

        var missing = LlmTools.EveryCapability.Except(byCapability.Keys)
            .Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new AssistantTextUnavailableException(
                $"{path} declares no tool for {missing.Count} capability(ies) the assistant can " +
                $"dispatch: {string.Join(", ", missing)}. Each needs an entry naming it; restore them " +
                "or reinstall the file from the deploy.");

        var unknown = byCapability.Keys.Except(LlmTools.EveryCapability)
            .Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new AssistantTextUnavailableException(
                $"{path} declares {unknown.Count} capability(ies) this assistant has no handler for: " +
                $"{string.Join(", ", unknown)}. A tool the model is offered but nothing can run fails " +
                "the turn it is called on; remove them or correct the capability id.");

        _names = byCapability.ToDictionary(kv => kv.Key, kv => new Tool(kv.Value));
        _capabilities = byCapability.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
        _labels = byCapability.ToDictionary(
            kv => kv.Value, kv => byName[kv.Value].Label?.Trim(), StringComparer.Ordinal);

        LlmToolDefinition Define(Capability capability)
        {
            var name = _names[capability];
            return byName[name.Name].ToDefinition(name, path);
        }

        StagedCommandTools = LlmTools.StagedCommandsTier.Select(c => _names[c]).ToHashSet();
        AuthorizedReadTools = LlmTools.AuthorizedReadOnlyTier.Select(c => _names[c]).ToHashSet();

        ReadOnly = LlmTools.ReadOnlyTier.Select(Define).ToArray();
        All = LlmTools.ReadOnlyTier
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
    public string? LabelOf(Tool tool) =>
        _labels.TryGetValue(tool.Name, out var l) && !string.IsNullOrWhiteSpace(l) ? l : null;

    /// <inheritdoc />
    public IReadOnlySet<Tool> StagedCommandTools { get; }

    /// <inheritdoc />
    public IReadOnlySet<Tool> AuthorizedReadTools { get; }

    private static IReadOnlyDictionary<string, ToolDoc> Parse(string path)
    {
        if (!File.Exists(path))
            throw new AssistantTextUnavailableException(
                $"{path} does not exist. The assistant's tool definitions live on disk — run " +
                "deploy/deploy.sh to install them.");

        Dictionary<string, ToolDoc>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, ToolDoc>>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new AssistantTextUnavailableException($"{path} is not valid JSON: {ex.Message}");
        }

        if (parsed is null || parsed.Count == 0)
            throw new AssistantTextUnavailableException($"{path} describes no tools.");

        return parsed;
    }

    private sealed record ParamDoc(
        string? Name,
        string? Description,
        bool Required = true,
        string Type = "string",
        [property: JsonPropertyName("enum")] IReadOnlyList<string>? Enum = null)
    {
        public LlmToolParameter ToParameter(string tool, string path)
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new AssistantTextUnavailableException(
                    $"{path}: a parameter of '{tool}' has no name.");

            if (string.IsNullOrWhiteSpace(Description))
                throw new AssistantTextUnavailableException(
                    $"{path}: parameter '{Name}' of '{tool}' has no description. The description is " +
                    "what the model routes on; an empty one is not a valid tuning.");

            if (!KnownTypes.Contains(Type))
                throw new AssistantTextUnavailableException(
                    $"{path}: parameter '{Name}' of '{tool}' has type '{Type}', which is not one of " +
                    $"{string.Join(", ", KnownTypes.Order(StringComparer.Ordinal))}.");

            return new LlmToolParameter(Name, Description.Trim(), Required, Type,
                Enum is { Count: > 0 } ? Enum : null);
        }
    }

    private sealed record ToolDoc(
        string? Capability, string? Label, string? Description, IReadOnlyList<ParamDoc>? Params)
    {
        public LlmToolDefinition ToDefinition(Tool tool, string path)
        {
            if (string.IsNullOrWhiteSpace(Description))
                throw new AssistantTextUnavailableException(
                    $"{path}: tool '{tool.Name}' has no description. The description is how the model " +
                    "knows when to call it; an empty one silently removes the tool from play.");

            var parameters = (Params ?? [])
                .Select(p => p.ToParameter(tool.Name, path))
                .ToArray();

            return new LlmToolDefinition(tool, Description.Trim(), parameters);
        }
    }
}
