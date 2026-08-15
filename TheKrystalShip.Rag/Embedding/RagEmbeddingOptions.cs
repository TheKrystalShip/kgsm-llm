using TheKrystalShip.KGSM.LeafConfig;

namespace TheKrystalShip.Rag.Embedding;

/// <summary>
/// Which local server produces embedding vectors. Independent of the chat model's backend: the
/// index is a different model on a different endpoint, and the two are configured separately.
/// </summary>
public enum EmbeddingProvider
{
    /// <summary>Ollama's native <c>/api/embed</c>.</summary>
    Ollama,

    /// <summary>llama.cpp's <c>llama-server</c>, started with <c>--embedding</c>, over <c>/v1/embeddings</c>.</summary>
    LlamaCpp
}

/// <summary>
/// Configuration for the embedding client. Bound from the <c>Rag</c> config section
/// (a subset of the wider RagOptions the host adds in Phase 2).
/// </summary>
[LeafSection(Section)]
public sealed class RagEmbeddingOptions
{
    public const string Section = "Rag";

    /// <summary>Which inference server produces the vectors.</summary>
    /// <panel>Which local server turns text into vectors. Set separately from the chat model's server —
    /// the index is its own model and may be served from somewhere else entirely.</panel>
    [LeafField("ragEmbeddingProvider", "Embedding server", Group = "rag", Risk = LeafRisk.Wiring,
        DependsOn = "ragEnabled")]
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.Ollama;

    /// <summary>Base URL of the embedding server, e.g. http://localhost:11434</summary>
    /// <panel>Where the embedding model is served from. Usually the same server as the chat model.</panel>
    [LeafField("ragEmbeddingEndpoint", "Embedding endpoint", Group = "rag", Risk = LeafRisk.Wiring,
        DependsOn = "ragEnabled")]
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Embedding model tag. Default <c>embeddinggemma</c> — Google's Gemma-native on-device
    /// embedder, paired with the gemma4 chat model (D5). Separate from the chat model — a small,
    /// dedicated embedder that sits alongside it in VRAM (plan §6). Alternatives:
    /// <c>nomic-embed-text</c>, <c>bge-m3</c>. Changing this invalidates an existing index.
    /// </summary>
    /// <panel>Model used to turn text into the vectors searches compare. It has to be the same model the
    /// index was built with, or every search scores as unrelated.</panel>
    [LeafField("ragEmbeddingModel", "Embedding model", Group = "rag", Risk = LeafRisk.Wiring,
        DependsOn = "ragEnabled")]
    public string EmbeddingModel { get; set; } = "embeddinggemma";

    /// <summary>Request timeout in seconds.</summary>
    /// <panel>How long to wait for the embedding model when turning a question into a vector.</panel>
    [LeafField("ragEmbeddingTimeoutSec", "Embedding timeout", Group = "rag", Min = 1, Unit = "s",
        DependsOn = "ragEnabled")]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Optional override for the document (index-time) task prefix. When null, it's resolved from
    /// the model name (<see cref="EmbeddingPrefixes"/>). Most modern embedders are asymmetric —
    /// <c>nomic-embed-text</c> needs <c>search_document:</c> for chunks and <c>search_query:</c>
    /// for queries; raw /api/embed does not apply these, and omitting them measurably hurts
    /// retrieval. The prefix *strings* are a tuning detail; the document/query *distinction* is
    /// baked into the API shape (plan, advisor).
    /// </summary>
    /// <panel>Text prepended to a passage before it is embedded, for models trained to expect one. It has
    /// to match what the index was built with.</panel>
    [LeafField("ragDocumentPrefix", "Document prefix", Group = "rag", Risk = LeafRisk.Wiring,
        DependsOn = "ragEnabled", NoDefault = true)]
    public string? DocumentPrefix { get; set; }

    /// <summary>Optional override for the query (search-time) task prefix. Null → resolve from the model name.</summary>
    /// <panel>Text prepended to a question before it is embedded, for models trained to expect one. It has
    /// to match what the index was built with.</panel>
    [LeafField("ragQueryPrefix", "Query prefix", Group = "rag", Risk = LeafRisk.Wiring,
        DependsOn = "ragEnabled", NoDefault = true)]
    public string? QueryPrefix { get; set; }
}
