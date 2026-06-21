using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One retrieval hit from the local knowledge index: the chunk text, where it came from, the
/// heading breadcrumb that scoped it, and its similarity to the query (cosine, -1..1). Like a
/// <see cref="WebSearchHit"/> this is external grounding (cite-able, possibly stale), never a
/// measured fact about this host — but unlike web search it comes from the operator's own
/// indexed docs corpus, not a third-party provider.
/// </summary>
public sealed record RetrievedChunk(string SourcePath, string HeaderPath, string Text, double Score);

/// <summary>
/// Local retrieval-augmented grounding — a leaf capability that answers "what do the operator's
/// indexed docs say about X" by semantic search over a pre-built vector index. The host supplies
/// the adapter (a Native-AOT <c>TheKrystalShip.Rag</c>-backed one today); the index is produced
/// out-of-band by the standalone indexer (plan §D6). Deliberately NOT routed through kgsm-lib:
/// that chokepoint is for the kgsm engine, and retrieval is a knowledge concern local to the
/// assistant. Implementations MUST NOT throw — return a failed <see cref="Result"/> instead.
/// </summary>
public interface IRetrieval
{
    Task<Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(
        string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IRetrieval"/> for hosts that have not enabled RAG: every retrieval fails
/// closed with a clear reason, so embedding the assistant library never breaks DI just because
/// retrieval isn't configured (plan §D7 — fail-closed + omit-when-disabled). <c>AddKgsmAssistant</c>
/// registers this with <c>TryAddSingleton</c>; a host that enables RAG registers a concrete adapter
/// afterward (<c>AddKgsmAdapters</c> when <c>Rag:Enabled=true</c>) — that later registration wins.
/// </summary>
internal sealed class DisabledRetrieval : IRetrieval
{
    public Task<Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(
        string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<RetrievedChunk>>("local retrieval is not enabled on this host"));
}
