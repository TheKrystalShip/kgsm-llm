namespace TheKrystalShip.Rag.Chunking;

/// <summary>
/// A pre-embedding chunk: the text to embed (already prefixed with its heading breadcrumb),
/// the breadcrumb itself, and the source path. The embedder turns <see cref="Text"/> into an
/// <see cref="Index.IndexedChunk"/>.
/// </summary>
/// <param name="SourcePath">Source file this chunk came from.</param>
/// <param name="HeaderPath">Heading breadcrumb, e.g. "Title &gt; Section" (empty if the doc had no headings above it).</param>
/// <param name="Text">Embed text: <c>HeaderPath</c> (if any) + body. The breadcrumb gives the embedding scope context.</param>
public sealed record Chunk(string SourcePath, string HeaderPath, string Text);
