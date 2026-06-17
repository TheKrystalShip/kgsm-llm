namespace TheKrystalShip.Llm.Recording;

/// <summary>
/// Configuration for the conversation transcript corpus (offline self-improvement analysis).
/// Disabled by default; a surface opts in and supplies a writable <see cref="Directory"/>.
/// </summary>
public class RecordingOptions
{
    public const string Section = "Recording";

    /// <summary>
    /// Master switch. When false (the default) a no-op recorder is registered and the agent loop
    /// builds no per-turn record at all.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Directory the daily <c>yyyy-MM-dd.jsonl</c> transcript files are appended to. The host
    /// supplies this (e.g. the CLI defaults it to an XDG data location); the generic library holds
    /// no host-shaped path knowledge. If empty while <see cref="Enabled"/> is true, recording falls
    /// back to the no-op (nothing to write to).
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}
