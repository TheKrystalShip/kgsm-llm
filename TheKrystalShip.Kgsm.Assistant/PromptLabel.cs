namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Renders a server's display name for the places the model reads it back: the instance
/// list injected into the system prompt every turn, and the refusal messages that list the
/// known servers.
/// </summary>
/// <remarks>
/// <para>
/// A display name is free text an operator sets, and it lands verbatim in front of the model
/// for every person on every turn. Control characters are stripped before it is ever stored, so
/// it is one line; what is left that could confuse the model is the double quote that would end
/// the value early and let the rest of the label read as prose <em>outside</em> it — the classic
/// "close the quote and append a sentence" break-out. Escaping the backslash and the quote keeps
/// the whole label inside one quoted value, so it cannot forge the surrounding structure or a
/// second list entry.
/// </para>
/// <para>
/// This does not — cannot — stop a label whose text reads as a fluent instruction inside its own
/// quotes; escaping is the structural half. The standing note beside the list, that a quoted
/// display name is a name and never a command, is the semantic half. Together they are the
/// mitigation, not a guarantee: a display name is untrusted input and is treated as data here on
/// purpose.
/// </para>
/// </remarks>
internal static class PromptLabel
{
    /// <summary>
    /// The label as a quoted, escaped value — <c>"…"</c> with any inner backslash and double
    /// quote escaped. A label with neither renders identically to a plain quoted string, so an
    /// ordinary display name is unchanged.
    /// </summary>
    internal static string Quoted(string? label) =>
        "\"" + (label ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
