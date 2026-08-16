namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// One tunable system-prompt segment: the file it is read from, and the inline config key that can
/// override that file for a host which would rather set it as an environment variable.
/// <para>
/// There is no in-code default. The prompt is content, not logic — it is edited far more often than
/// the code around it, and a constant compiled into the assembly can only be changed by a rebuild
/// and a redeploy. Keeping one copy on disk also removes the failure where a host is quietly running
/// text nobody can see, because the file it was supposed to read was never installed.
/// </para>
/// </summary>
internal sealed record PromptSegment(string FileName, string ConfigKey);

/// <summary>
/// The editable system-prompt segments, shared by the builder (which resolves the effective text per
/// turn) and the startup check (which refuses to run without them).
/// </summary>
internal static class PromptSegments
{
    public static readonly PromptSegment Preamble = new("preamble.md", "Llm:Preamble");

    public static readonly PromptSegment ActionsAllowed = new("actions-allowed.md", "Llm:ActionsAllowed");

    public static readonly PromptSegment ActionsAuto = new("actions-auto.md", "Llm:ActionsAuto");

    public static readonly PromptSegment ActionsDenied = new("actions-denied.md", "Llm:ActionsDenied");

    /// <summary>Appended only when the caller asked for <see cref="ReplyStyle.Voice"/>.</summary>
    public static readonly PromptSegment Voice = new("voice.md", "Llm:Voice");

    public static readonly IReadOnlyList<PromptSegment> All =
        [Preamble, ActionsAllowed, ActionsAuto, ActionsDenied, Voice];
}
