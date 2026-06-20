namespace TheKrystalShip.Rag.Ollama;

/// <summary>
/// Configuration for the Ollama embedding client. Bound from the <c>Rag</c> config section
/// (a subset of the wider RagOptions the host adds in Phase 2).
/// </summary>
public sealed class RagEmbeddingOptions
{
    public const string Section = "Rag";

    /// <summary>Base URL of the Ollama server, e.g. http://localhost:11434</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Embedding model tag. Default <c>embeddinggemma</c> — Google's Gemma-native on-device
    /// embedder, paired with the gemma4 chat model (D5). Separate from the chat model — a small,
    /// dedicated embedder that sits alongside it in VRAM (plan §6). Alternatives:
    /// <c>nomic-embed-text</c>, <c>bge-m3</c>. Changing this invalidates an existing index.
    /// </summary>
    public string EmbeddingModel { get; set; } = "embeddinggemma";

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Optional override for the document (index-time) task prefix. When null, it's resolved from
    /// the model name (<see cref="EmbeddingPrefixes"/>). Most modern embedders are asymmetric —
    /// <c>nomic-embed-text</c> needs <c>search_document:</c> for chunks and <c>search_query:</c>
    /// for queries; raw /api/embed does not apply these, and omitting them measurably hurts
    /// retrieval. The prefix *strings* are a tuning detail; the document/query *distinction* is
    /// baked into the API shape (plan, advisor).
    /// </summary>
    public string? DocumentPrefix { get; set; }

    /// <summary>Optional override for the query (search-time) task prefix. Null → resolve from the model name.</summary>
    public string? QueryPrefix { get; set; }
}
