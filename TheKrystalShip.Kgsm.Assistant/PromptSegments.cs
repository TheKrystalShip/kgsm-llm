namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// One tunable system-prompt segment: the editable file that overrides it (highest precedence), the
/// inline config key (middle), and the in-code constant default (lowest, always present).
/// </summary>
internal sealed record PromptSegment(string FileName, string ConfigKey, string Default);

/// <summary>
/// The three editable system-prompt segments, the single source of truth shared by the builder (which
/// resolves the effective text per turn) and <see cref="PromptScaffold"/> (which seeds default files).
/// </summary>
internal static class PromptSegments
{
    public static readonly PromptSegment Preamble =
        new("preamble.md", "Llm:Preamble", KgsmAssistantPrompts.Preamble);

    public static readonly PromptSegment ActionsAllowed =
        new("actions-allowed.md", "Llm:ActionsAllowed", KgsmAssistantPrompts.ActionsAllowed);

    public static readonly PromptSegment ActionsDenied =
        new("actions-denied.md", "Llm:ActionsDenied", KgsmAssistantPrompts.ActionsDenied);

    public static readonly IReadOnlyList<PromptSegment> All = new[] { Preamble, ActionsAllowed, ActionsDenied };
}
