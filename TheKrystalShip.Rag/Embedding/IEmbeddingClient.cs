using TheKrystalShip.Rag.Models;

namespace TheKrystalShip.Rag.Embedding;

/// <summary>
/// Produces embedding vectors via Ollama. The document/query split is first-class because most
/// embedders are asymmetric (see <see cref="EmbeddingPrefixes"/>): index chunks as documents,
/// search text as queries — mixing them degrades retrieval.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>The configured embedding model tag — stamped into the index header so a reader can reject a mismatch.</summary>
    string ModelName { get; }

    /// <summary>Embeds index-time documents (in order). Empty input returns an empty list.</summary>
    Task<Result<IReadOnlyList<float[]>>> EmbedDocumentsAsync(
        IReadOnlyList<string> documents, CancellationToken cancellationToken = default);

    /// <summary>Embeds a single search-time query.</summary>
    Task<Result<float[]>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);
}
