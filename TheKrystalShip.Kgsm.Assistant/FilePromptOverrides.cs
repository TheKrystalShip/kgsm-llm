using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Llm.Models;

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
    public const string ToolsFileName = "tools.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _directory;
    private readonly ILogger<FilePromptOverrides> _logger;

    public FilePromptOverrides(IConfiguration configuration, ILogger<FilePromptOverrides> logger)
    {
        var dir = configuration[DirectoryKey];
        _directory = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _logger = logger;
    }

    public string? ReadText(string fileName)
    {
        if (_directory is null)
            return null;

        var path = Path.Combine(_directory, fileName);
        try
        {
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path).Trim();
            // Blank counts as absent — a mid-save truncation falls back to the default for one turn
            // rather than blanking the prompt.
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read prompt override {File}; using default", path);
            return null;
        }
    }

    public IReadOnlyList<LlmToolDefinition> OverlayTools(IReadOnlyList<LlmToolDefinition> tools)
    {
        if (_directory is null)
            return tools;

        var path = Path.Combine(_directory, ToolsFileName);
        Dictionary<Tool, ToolTextOverride?>? overrides;
        try
        {
            if (!File.Exists(path))
                return tools;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return tools;

            // JSON keys are strings; convert to Tool instances for typed lookup.
            var raw = JsonSerializer.Deserialize<Dictionary<string, ToolTextOverride?>>(json, Json);
            overrides = raw?.ToDictionary(kv => new Tool(kv.Key), kv => kv.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read tool-description overrides {File}; using defaults", path);
            return tools;
        }

        if (overrides is null || overrides.Count == 0)
            return tools;

        return tools.Select(t => Apply(t, overrides)).ToArray();
    }

    /// <summary>Overlays a single tool's description/param-descriptions; the name is never touched.</summary>
    private static LlmToolDefinition Apply(LlmToolDefinition tool, IReadOnlyDictionary<Tool, ToolTextOverride?> overrides)
    {
        if (!overrides.TryGetValue(tool.Tool, out var o) || o is null)
            return tool;

        var description = string.IsNullOrWhiteSpace(o.Description) ? tool.Description : o.Description!.Trim();

        var parameters = tool.Parameters;
        if (o.Params is { Count: > 0 })
        {
            parameters = tool.Parameters
                .Select(p => o.Params.TryGetValue(p.Name, out var pd) && !string.IsNullOrWhiteSpace(pd)
                    ? p with { Description = pd!.Trim() }
                    : p)
                .ToArray();
        }

        return tool with { Description = description, Parameters = parameters };
    }

    private sealed class ToolTextOverride
    {
        public string? Description { get; set; }
        public Dictionary<string, string?>? Params { get; set; }
    }
}
