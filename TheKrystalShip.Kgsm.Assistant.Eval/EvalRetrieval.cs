using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// An <see cref="IRetrieval"/> over an in-memory <see cref="RagIndex"/> the eval just built — a faithful
/// mirror of the production <c>RagRetrieval</c> (embed query → cosine top-k → MinScore floor → translate
/// the Rag <c>Result</c> to the assistant <c>Result</c>), with one difference: the index is held in
/// memory and the <see cref="VectorSearch.TopK">TopK</see>/<see cref="_minScore">MinScore</see> knobs are
/// constructor args, so a sweep can vary them per run without rebuilding DI or touching disk. Driving the
/// real <see cref="SearchAggregator"/> over THIS keeps the grounding format, MaxContextChars cap, and
/// LocalMinScore behaviour identical to what ships — so the knob values the sweep picks actually transfer.
/// Never throws (the port contract).
/// </summary>
internal sealed class EvalRetrieval : IRetrieval
{
    private readonly IEmbeddingClient _embeddings;
    private readonly RagIndex _index;
    private readonly int _topK;
    private readonly double _minScore;

    /// <summary>
    /// The raw cosine top-k from the most recent <see cref="RetrieveAsync"/> call, captured BEFORE the
    /// MinScore filter so the diagnosis can measure honest recall@k (Phase 6). Read by the runner right
    /// after it drives the aggregator; not part of the port contract.
    /// </summary>
    public IReadOnlyList<SearchHit> LastRawHits { get; private set; } = [];

    public EvalRetrieval(IEmbeddingClient embeddings, RagIndex index, int topK, double minScore)
    {
        _embeddings = embeddings;
        _index = index;
        _topK = topK;
        _minScore = minScore;
    }

    public async Task<Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result.Failure<IReadOnlyList<RetrievedChunk>>("the retrieval query was empty");

        var queryEmbedding = await _embeddings.EmbedQueryAsync(query, cancellationToken);
        if (queryEmbedding.IsFailure)
            return Result.Failure<IReadOnlyList<RetrievedChunk>>(queryEmbedding.Error!);

        var queryVector = queryEmbedding.Value!;
        if (queryVector.Length != _index.Dimension)
            return Result.Failure<IReadOnlyList<RetrievedChunk>>(
                "the retrieval index dimension does not match the embedding model and must be rebuilt");

        var hits = VectorSearch.TopK(_index.Chunks, queryVector, _topK);
        LastRawHits = hits;  // captured pre-MinScore-filter for the diagnosis (recall@k)
        var results = hits
            .Where(h => h.Score >= _minScore)
            .Select(h => new RetrievedChunk(h.Chunk.SourcePath, h.Chunk.HeaderPath, h.Chunk.Text, h.Score))
            .ToArray();

        return Result.Success<IReadOnlyList<RetrievedChunk>>(results);
    }
}
