namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A tool invocation requested by the model. Arguments are coerced to strings;
/// tool arguments in this model are treated as string values.
/// </summary>
public record LlmToolCall(string Name, IReadOnlyDictionary<string, string?> Arguments)
{
    /// <summary>Returns the named argument, or null if absent.</summary>
    public string? Arg(string key) =>
        Arguments.TryGetValue(key, out var value) ? value : null;
}
