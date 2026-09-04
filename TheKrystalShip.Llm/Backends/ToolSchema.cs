using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends;

/// <summary>
/// A tool as both backends are told about it: <c>{"type":"function","function":{…}}</c>.
/// </summary>
/// <remarks>
/// The shape is declared rather than assembled from anonymous objects because the request bodies it
/// sits inside are serialized through a source-generated context. A type the generator cannot see is
/// a type that cannot be sent from a trimmed or ahead-of-time-compiled host.
/// </remarks>
public sealed record ToolFunctionPayload
{
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public ToolFunctionBody Function { get; init; } = new();
}

public sealed record ToolFunctionBody
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("parameters")] public ToolParameterSet Parameters { get; init; } = new();
}

public sealed record ToolParameterSet
{
    [JsonPropertyName("type")] public string Type { get; init; } = "object";
    [JsonPropertyName("properties")] public Dictionary<string, ToolParameterSchema> Properties { get; init; } = [];
    [JsonPropertyName("required")] public List<string> Required { get; init; } = [];
}

/// <summary>One parameter's JSON schema.</summary>
/// <remarks>
/// <see cref="Enum"/> is the closed set of values a parameter accepts, which steers the model to a
/// valid one and is the reliability lever for a small local model on a categorical parameter. A
/// parameter without one leaves the value unconstrained, and the key is left out entirely rather
/// than sent empty.
/// </remarks>
public sealed record ToolParameterSchema
{
    [JsonPropertyName("type")] public string Type { get; init; } = "string";
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; init; }
}

/// <summary>
/// Renders a tool definition as the JSON-schema function object every backend expects. Ollama and
/// llama-server take the same shape here, so both build it from this one place — a tool that is
/// described differently to two backends is a tool whose routing cannot be compared between them.
/// </summary>
public static class ToolSchema
{
    /// <summary>Wraps a tool as <c>{"type":"function","function":{…}}</c>.</summary>
    public static ToolFunctionPayload BuildFunction(LlmToolDefinition tool) => new()
    {
        Function = new ToolFunctionBody
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = new ToolParameterSet
            {
                Properties = tool.Parameters.ToDictionary(p => p.Name, BuildParameterSchema),
                Required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList(),
            },
        },
    };

    private static ToolParameterSchema BuildParameterSchema(LlmToolParameter p) => new()
    {
        Type = p.Type,
        Description = p.Description,
        Enum = p.AllowedValues is { Count: > 0 } values ? [.. values] : null,
    };
}
