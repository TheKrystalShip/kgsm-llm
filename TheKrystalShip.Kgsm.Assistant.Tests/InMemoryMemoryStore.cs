using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// An <see cref="IMemoryStore"/> held in a dictionary — the durable store's resolved behaviour without
/// a database file. Its append-only shape is not modelled, because nothing above the store can observe
/// it: a caller sees only what stands, which is what the tests here assert on.
/// (<c>SqliteMemoryStoreTests</c> is what covers the log itself.)
/// </summary>
internal sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly Dictionary<string, Dictionary<string, MemoryRecord>> _byOwner = [];
    private readonly int _maxPerOwner;

    public InMemoryMemoryStore(int maxPerOwner = 64) => _maxPerOwner = maxPerOwner;

    private Dictionary<string, MemoryRecord> Owner(string ownerKey) =>
        _byOwner.TryGetValue(ownerKey, out var owned) ? owned : _byOwner[ownerKey] = [];

    public IReadOnlyList<MemoryRecord> List(string ownerKey) =>
        string.IsNullOrEmpty(ownerKey)
            ? []
            : [.. Owner(ownerKey).Values.OrderByDescending(m => m.WrittenAt).ThenBy(m => m.Key)];

    public MemoryRecord? Get(string ownerKey, string key) =>
        string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(key)
            ? null
            : Owner(ownerKey).GetValueOrDefault(key);

    public bool Write(string ownerKey, MemoryRecord memory)
    {
        if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(memory.Key))
            return false;

        var owned = Owner(ownerKey);
        if (!owned.ContainsKey(memory.Key) && owned.Count >= _maxPerOwner)
            return false;

        owned[memory.Key] = memory;
        return true;
    }

    public bool Forget(string ownerKey, string key) =>
        !string.IsNullOrEmpty(ownerKey) && !string.IsNullOrEmpty(key) && Owner(ownerKey).Remove(key);

    public int Count(string ownerKey) => string.IsNullOrEmpty(ownerKey) ? 0 : Owner(ownerKey).Count;
}
