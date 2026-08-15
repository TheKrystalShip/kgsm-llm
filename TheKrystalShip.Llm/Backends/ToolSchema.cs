using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends;

/// <summary>
/// Renders a tool definition as the JSON-schema function object every backend expects. Ollama and
/// llama-server take the same shape here, so both build it from this one place — a tool that is
/// described differently to two backends is a tool whose routing cannot be compared between them.
/// </summary>
public static class ToolSchema
{
    /// <summary>Wraps a tool as <c>{"type":"function","function":{…}}</c>.</summary>
    public static object BuildFunction(LlmToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = new
            {
                type = "object",
                properties = tool.Parameters.ToDictionary(p => p.Name, BuildParameterSchema),
                required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
            }
        }
    };

    /// <summary>
    /// Builds one parameter's JSON-schema object. A parameter with
    /// <see cref="LlmToolParameter.AllowedValues"/> gains an <c>enum</c> constraint,
    /// which steers the model to a valid value (the small-model reliability lever).
    /// </summary>
    private static object BuildParameterSchema(LlmToolParameter p)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = p.Type,
            ["description"] = p.Description,
        };
        if (p.AllowedValues is { Count: > 0 })
            schema["enum"] = p.AllowedValues;
        return schema;
    }
}
