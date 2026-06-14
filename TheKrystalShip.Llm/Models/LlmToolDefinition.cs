namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A single parameter of a tool. JSON-schema details are produced by the
/// backend client; this stays transport-agnostic.
/// </summary>
/// <param name="AllowedValues">
/// Optional closed set of permitted values. When non-empty, the backend client
/// emits it as the JSON-schema <c>enum</c> constraint so the model is steered to a
/// valid value — the reliability lever for small local models on a categorical
/// parameter (e.g. a command verb). Null/empty leaves the value unconstrained.
/// </param>
public record LlmToolParameter(
    string Name,
    string Description,
    bool Required = true,
    string Type = "string",
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>
/// Declarative definition of a tool the model may call. The set of definitions
/// passed to the client IS the whitelist of callable tools.
/// </summary>
public record LlmToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<LlmToolParameter> Parameters)
{
    public static LlmToolDefinition Create(
        string name, string description, params LlmToolParameter[] parameters) =>
        new(name, description, parameters);
}
