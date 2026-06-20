namespace TheKrystalShip.Rag.Index;

/// <summary>
/// An in-memory RAG index: the header that makes it self-describing (and the decoupling
/// contract between the independently-deployed indexer and assistant — plan §D9), the embedded
/// chunks, and the source-file manifest for incremental re-index.
/// <para>
/// The reader (assistant retrieval) compares <see cref="EmbeddingModel"/> / <see cref="Dimension"/>
/// against its own configuration and rebuilds on mismatch — a different embedding model means a
/// different vector space, so the vectors are meaningless, never migratable.
/// </para>
/// </summary>
public sealed class RagIndex
{
    /// <summary>
    /// On-disk format version. Bumped only on a breaking layout change; the reader rejects any
    /// other version (<see cref="RagIndexFormatException"/>) and the caller re-indexes.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The Ollama embedding model the vectors were produced with (e.g. <c>nomic-embed-text</c>).</summary>
    public required string EmbeddingModel { get; init; }

    /// <summary>Embedding dimension; every chunk vector has exactly this length.</summary>
    public required int Dimension { get; init; }

    /// <summary>Chunk target size (characters) used at index time — recorded for diagnostics/rebuild decisions.</summary>
    public required int ChunkSize { get; init; }

    /// <summary>Chunk overlap (characters) used at index time.</summary>
    public required int ChunkOverlap { get; init; }

    /// <summary>The embedded chunks, in manifest order (each <see cref="SourceFileEntry"/> owns a contiguous slice).</summary>
    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    /// <summary>Per-source-file manifest (content hash + chunk range) driving incremental re-index.</summary>
    public required IReadOnlyList<SourceFileEntry> Manifest { get; init; }
}
