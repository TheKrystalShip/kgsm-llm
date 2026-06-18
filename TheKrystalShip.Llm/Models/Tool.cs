namespace TheKrystalShip.Llm.Models;

/// <summary>
/// A type-safe identifier for an LLM tool. Replaces raw string names throughout
/// the codebase so tool references are validated at compile time rather than
/// silently drifting as magic strings.
/// Equality is by <see cref="Name"/> (case-sensitive).
/// </summary>
public sealed record Tool(string Name)
{
    public override string ToString() => Name;
}
