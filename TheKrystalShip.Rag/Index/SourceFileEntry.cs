namespace TheKrystalShip.Rag.Index;

/// <summary>
/// Manifest entry for one indexed source file: its content hash (for incremental re-index —
/// skip re-embedding files whose hash is unchanged) and the contiguous range of
/// <see cref="RagIndex.Chunks"/> it produced.
/// <para>
/// "Incremental" means skip the embed calls, not patch the file in place: every re-index still
/// fully rewrites the index (temp → atomic rename, plan §D8), so positional chunk ranges are
/// valid — the daemon rebuilds the in-memory index (drop a changed file's chunks, append the
/// re-embedded ones) and writes the whole thing.
/// </para>
/// </summary>
/// <param name="Path">Source file path.</param>
/// <param name="ContentHash">Hash of the file's content at index time (e.g. hex SHA-256).</param>
/// <param name="ChunkOffset">Index into <see cref="RagIndex.Chunks"/> of this file's first chunk.</param>
/// <param name="ChunkCount">Number of contiguous chunks this file produced.</param>
public sealed record SourceFileEntry(string Path, string ContentHash, int ChunkOffset, int ChunkCount);
