namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// Inputs for one index build: which sources to read, how to match files, and the chunking knobs.
/// Host-agnostic — the standalone indexer fills it from CLI flags, the assistant CLI from its
/// <c>Rag</c> config; both feed the same <see cref="IndexBuilder"/>.
/// </summary>
public sealed class IndexBuilderOptions
{
    /// <summary>Source files and/or directories to index. Directories are walked recursively for <see cref="SearchPattern"/>.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Glob applied when walking a source directory. Default <c>*.md</c> (the docs corpus, D2).</summary>
    public string SearchPattern { get; init; } = "*.md";

    /// <summary>Chunk target size in characters (passed to the chunker). Recorded in the index header.</summary>
    public int ChunkSize { get; init; } = 2000;

    /// <summary>Chunk overlap in characters. Recorded in the index header; must be &lt; <see cref="ChunkSize"/>.</summary>
    public int ChunkOverlap { get; init; } = 200;
}
