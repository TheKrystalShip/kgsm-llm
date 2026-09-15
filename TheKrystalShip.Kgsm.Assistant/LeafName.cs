using TheKrystalShip.Agent.Prompts;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Validates the name of a leaf calling the assistant (<c>kgsm-bot</c>, <c>kgsm-api</c>), which
/// selects that leaf's prompt overrides and, on the hosting side, the audit origin its surface
/// records under.
/// </summary>
/// <remarks>
/// <para>
/// The name arrives over the wire and is used as a <em>path segment</em>, so this
/// <b>rejects rather than repairs</b>. A sanitizer that strips illegal characters turns
/// <c>kgsm/bot</c> into a lookup against a directory named <c>kgsmbot</c> — a silent misread, where
/// refusing simply falls through to the assistant's own prompts, which is the documented behaviour
/// for a leaf that has no overrides. It also means no accepted name can contain <c>.</c>,
/// <c>/</c> or <c>\</c>, so a traversal cannot be assembled out of parts that individually pass.
/// </para>
/// <para>
/// The relay path is already authenticated by a shared secret before any of this runs; refusing
/// here is the second lock, not the first.
/// </para>
/// </remarks>
public static class LeafName
{
    /// <summary>Upper bound on a leaf name; the longest deployed one is 14 characters.</summary>
    public const int MaxLength = PromptDirectory.MaxVariantLength;

    /// <summary>
    /// Returns <paramref name="raw"/> when it is a well-formed leaf name — lowercase ASCII letters,
    /// digits and hyphens, starting with a letter or digit — and <see langword="null"/> for anything
    /// else, including null, blank, and any name carrying a path separator or a dot. A
    /// <see langword="null"/> result means "no leaf": the caller falls back to the assistant's own
    /// prompts and its own origin.
    /// </summary>
    /// <remarks>
    /// A leaf is the variant its prompt text is read under, so the rule is the prompt directory's: one
    /// rule, because a name accepted as an origin and refused as a variant would record one surface
    /// while reading another's text.
    /// </remarks>
    public static string? Validate(string? raw) => PromptDirectory.ValidVariant(raw);
}
