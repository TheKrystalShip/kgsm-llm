namespace TheKrystalShip.Rag.Index;

/// <summary>
/// One embedded unit of a source document: the text that was embedded, where it came from,
/// the heading breadcrumb that scoped it, and its embedding vector.
/// </summary>
/// <param name="SourcePath">Path of the source file this chunk came from (as recorded at index time).</param>
/// <param name="HeaderPath">Heading breadcrumb, e.g. "Title &gt; Section &gt; Subsection" (empty if none).</param>
/// <param name="Text">The chunk text that was embedded (already includes the breadcrumb prefix, if any).</param>
/// <param name="Vector">The embedding vector; its length equals <see cref="RagIndex.Dimension"/>.</param>
public sealed record IndexedChunk(string SourcePath, string HeaderPath, string Text, float[] Vector);
