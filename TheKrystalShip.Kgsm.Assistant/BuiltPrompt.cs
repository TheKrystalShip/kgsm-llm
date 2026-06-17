namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The result of building a system prompt: the full assembled <see cref="Text"/> (persona + the live
/// instance/blueprint lists) sent to the model, and <see cref="TemplateHash"/> — a fingerprint of
/// only the EDITABLE template (the persona segments, excluding the injected lists). The hash is
/// recorded per turn so transcript analysis can bucket turns by prompt version: it moves when the
/// operator edits the prompt, not when inventory changes mid-session.
/// </summary>
public sealed record BuiltPrompt(string Text, string TemplateHash);
