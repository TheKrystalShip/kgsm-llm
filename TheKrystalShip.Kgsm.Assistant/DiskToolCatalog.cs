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

        // Every tool the code can dispatch must be described, and nothing else may be. The first
        // check catches a deleted entry (the model would lose a capability silently); the second
        // catches an invented one (the model would be offered a tool with no handler behind it and
        // could call it at will).
        var expected = LlmTools.EveryToolName.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(byName.Keys).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new AssistantTextUnavailableException(
                $"{path} is missing {missing.Count} tool(s) the assistant can dispatch: " +
                $"{string.Join(", ", missing)}. Every tool needs an entry; restore them or reinstall " +
                "the file from the deploy.");

        var unknown = byName.Keys.Except(expected).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new AssistantTextUnavailableException(
                $"{path} defines {unknown.Count} tool(s) this assistant has no handler for: " +
                $"{string.Join(", ", unknown)}. A tool the model is offered but nothing can run fails " +
                "the turn it is called on; remove them or spell the name as the code has it.");

        LlmToolDefinition Define(Tool tool) => byName[tool.Name].ToDefinition(tool, path);

        ReadOnly = LlmTools.ReadOnlyTier.Select(Define).ToArray();
        All = LlmTools.ReadOnlyTier
            .Concat(LlmTools.AuthorizedReadOnlyTier)
            .Concat(LlmTools.StagedCommandsTier)
            .Concat(LlmTools.AuthorizedActionsTier)
            .Where(t => t != LlmTools.ReviseBlueprint)
            .Select(Define)
            .ToArray();
        ReviseBlueprintTool = Define(LlmTools.ReviseBlueprint);
    }

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

    private sealed record ToolDoc(string? Description, IReadOnlyList<ParamDoc>? Params)
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
