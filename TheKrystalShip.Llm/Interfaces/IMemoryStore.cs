using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Interfaces;

/// <summary>
/// Durable memory: what the assistant wrote down in one conversation and reads back in later ones.
/// Distinct from <see cref="IConversationStore"/>, which is the transcript of one conversation and
/// ends where that conversation does — a memory is keyed to an <b>owner</b>
/// (<see cref="Conversation.MemoryScope"/>), so it crosses every chat that owner holds and reaches
/// nobody else's.
/// <para>
/// Append-only, resolved latest-wins per key — the same shape as the conversation store's tombstone,
/// verdict and preference entries. Rewriting a memory appends; forgetting one appends. Nothing is
/// ever updated or deleted in place, so the log keeps the fact that a memory changed and what it
/// used to say.
/// </para>
/// <para>
/// ⚠ Every method takes the owner key as its first argument and none of them derives one. The caller
/// resolves it from the conversation the turn is running in, which is server-derived — a key that
/// arrived from a client or from a model's tool arguments would let one person write into another's
/// memory.
/// </para>
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Every memory standing for <paramref name="ownerKey"/>, most recently written first. Forgotten
    /// ones are absent, and a rewritten one appears once, carrying what it says now. Empty for an
    /// owner that has never had anything written down — which is not an error, it is a new person.
    /// </summary>
    IReadOnlyList<MemoryRecord> List(string ownerKey);

    /// <summary>
    /// The memory filed under <paramref name="key"/>, or <see langword="null"/> when nothing stands
    /// there — never written, or forgotten since.
    /// </summary>
    MemoryRecord? Get(string ownerKey, string key);

    /// <summary>
    /// Writes <paramref name="memory"/> down, superseding whatever stood under its key. Returns
    /// <see langword="false"/> when the owner already holds
    /// <see cref="Conversation.MemoryOptions.MaxPerOwner"/> memories and this key is a new one — the
    /// caller reports that as a refusal naming the cap, because silently evicting the oldest would
    /// discard something a person asked to be kept.
    /// <para>
    /// Rewriting a key that already stands always succeeds, cap or no cap: it adds nothing to the
    /// count, and refusing it would leave an owner at the cap unable to correct a memory that is
    /// wrong.
    /// </para>
    /// </summary>
    bool Write(string ownerKey, MemoryRecord memory);

    /// <summary>
    /// Forgets the memory under <paramref name="key"/>, by appending a tombstone rather than removing
    /// anything. Returns <see langword="false"/> when nothing stood there, so the caller can say which
    /// keys do exist instead of reporting a success that changed nothing.
    /// </summary>
    bool Forget(string ownerKey, string key);

    /// <summary>How many memories stand for <paramref name="ownerKey"/> — the cap check, and what a
    /// surface shows without loading them.</summary>
    int Count(string ownerKey);
}
