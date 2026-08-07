using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// An inert <see cref="IPromptOverrides"/> for tests that don't exercise the override layer: no file
/// text, tools passed through unchanged (the same behavior as a real FilePromptOverrides with no
/// configured directory).
/// </summary>
internal sealed class PassthroughPromptOverrides : IPromptOverrides
{
    public string? ReadText(string fileName, string? leaf = null) => null;
    public IReadOnlyList<LlmToolDefinition> OverlayTools(IReadOnlyList<LlmToolDefinition> tools, string? leaf = null) => tools;
}
