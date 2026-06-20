namespace TheKrystalShip.Rag.Chunking;

/// <summary>
/// Knobs for <see cref="MarkdownChunker"/>. Sizes are in <b>characters</b> for Phase 1 (a
/// dependency-free proxy for tokens); token-accurate splitting is a later tuning upgrade.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>Target maximum chunk size in characters (excluding the breadcrumb prefix).</summary>
    public int ChunkSize { get; set; } = 2000;

    /// <summary>
    /// Characters of trailing context carried from one chunk into the next within the same
    /// section, so an answer split across a boundary isn't lost. Must be &lt; <see cref="ChunkSize"/>.
    /// </summary>
    public int ChunkOverlap { get; set; } = 200;
}
