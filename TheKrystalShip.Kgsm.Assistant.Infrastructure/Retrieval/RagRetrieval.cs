using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;

/// <summary>
/// <see cref="IRetrieval"/> backed by the Native-AOT <c>TheKrystalShip.Rag</c> core: embed the
/// query with the configured Ollama embedder, then brute-force cosine top-k over the on-disk index
/// (<see cref="RagIndexProvider"/>). Never throws — every failure (no/incompatible index, embedder
/// down, dimension mismatch) is a <see cref="Result.Failure{T}(string)"/>, matching the port's
/// contract and the §D7 fail-closed posture. The <c>Rag.Models.Result</c> the embedder returns is
/// translated to the assistant's <c>Llm.Models.Result</c> at this seam (the two cores share no
/// types — that is what keeps the embed core AOT-clean).
/// </summary>
internal sealed class RagRetrieval : IRetrieval
{
    private readonly IEmbeddingClient _embeddings;
    private readonly RagIndexProvider _index;
    private readonly RagOptions _options;
    private readonly ILogger<RagRetrieval> _logger;

    public RagRetrieval(
        IEmbeddingClient embeddings,
        RagIndexProvider index,
        IOptions<RagOptions> options,
        ILogger<RagRetrieval> logger)
    {
        _embeddings = embeddings;
        _index = index;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(
        string query, CancellationToken cancellationToken = default)
    {
        // Never-throw contract: the embed client rejects blank input with an exception, so guard here.
        if (string.IsNullOrWhiteSpace(query))
            return Result.Failure<IReadOnlyList<RetrievedChunk>>("the retrieval query was empty");

        var indexResult = _index.Get();
        if (indexResult.IsFailure)
            return Result.Failure<IReadOnlyList<RetrievedChunk>>(indexResult.Error!);
        var index = indexResult.Value!;

        // Rag.Models.Result -> Llm.Models.Result translation seam.
        var queryEmbedding = await _embeddings.EmbedQueryAsync(query, cancellationToken);
        if (queryEmbedding.IsFailure)
            return Result.Failure<IReadOnlyList<RetrievedChunk>>(queryEmbedding.Error!);
        var queryVector = queryEmbedding.Value!;

        // VectorSearch.TopK throws on a length mismatch — the never-throw contract forbids that. A
        // mismatch means the index and the live embedder disagree on the vector space (e.g. the model
        // changed under a same-named tag); fail closed and signal a rebuild.
        if (queryVector.Length != index.Dimension)
        {
            _logger.LogWarning(
                "Query embedding dimension {Query} does not match the index dimension {Index}; the index must be rebuilt.",
                queryVector.Length, index.Dimension);
            return Result.Failure<IReadOnlyList<RetrievedChunk>>(
                "the retrieval index dimension does not match the embedding model and must be rebuilt");
        }

        var hits = VectorSearch.TopK(index.Chunks, queryVector, _options.TopK);

        var results = hits
            .Where(h => h.Score >= _options.MinScore)
            .Select(h => new RetrievedChunk(h.Chunk.SourcePath, h.Chunk.HeaderPath, h.Chunk.Text, h.Score))
            .ToArray();

        return Result.Success<IReadOnlyList<RetrievedChunk>>(results);
    }
}
