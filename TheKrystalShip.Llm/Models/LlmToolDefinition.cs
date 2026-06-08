namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A single parameter of a tool. JSON-schema details are produced by the
/// backend client; this stays transport-agnostic.
/// </summary>
public record LlmToolParameter(
    string Name,
    string Description,
    bool Required = true,
    string Type = "string");

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
