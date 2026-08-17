namespace TheKrystalShip.Llm.Models;

/// <summary>
/// One thing the assistant wrote down about an owner, to be read back in later conversations.
/// <para>
/// ⚠ A memory carries what it was <b>told</b>, never what a tool <b>measured</b>. Preferences,
/// decisions, standing instructions, corrections and the words a person uses for things all keep their
/// meaning months later; a port, a version, a player count or a run-state does not, and a remembered
/// reading offered as current is a fabricated status. This is the same reason
/// <see cref="Conversation.ModelContextProjection"/> replays a past tool call without its output.
/// </para>
/// </summary>
/// <param name="Key">
/// The stable slug this memory is filed under, lowercase <c>[a-z0-9-]</c>. It is the whole update
/// mechanism: writing the same key again supersedes what stood there, so a memory is revised by
/// rewriting it rather than by an edit verb the store would have to reconcile.
/// </param>
/// <param name="Summary">
/// The one line that is injected into every turn's system prompt. It carries the whole cost of the
/// feature's context budget, so it states the fact rather than announcing that one exists.
/// </param>
/// <param name="Body">The full note, read on demand and never injected.</param>
/// <param name="WrittenAt">
/// When it was written. Shown wherever the memory is — a remembered claim that turns out to be stale
/// is at least visibly dated, which is the only honest thing available given nothing can re-measure a
/// preference.
/// </param>
/// <param name="Origin">
/// The conversation it was written in, or <c>null</c> when it was not written by a turn (a person
/// entering one by hand). Provenance only: it answers "where did it learn that" for a surface showing
/// the memory, and nothing resolves an owner from it.
/// </param>
public sealed record MemoryRecord(
    string Key,
    string Summary,
    string Body,
    DateTimeOffset WrittenAt,
    string? Origin);
