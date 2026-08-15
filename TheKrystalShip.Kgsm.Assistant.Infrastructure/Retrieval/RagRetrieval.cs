using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;

/// <summary>
/// <see cref="IRetrieval"/> backed by the Native-AOT <c>TheKrystalShip.Rag</c> core: embed the
/// query with the configured Ollama embedder, then brute-force cosine top-k over the on-disk index
/// (<see cref="RagIndexProvider"/>). Never throws — every failure (no/incompatible index, embedder
/// down, dimension mismatch) is a <see cref="Result.Failure{T}(string)"/>, matching the port's
/// contract and the fail-closed posture. The <c>Rag.Models.Result</c> the embedder returns is
/// translated to the assistant's <c>Llm.Models.Result</c> at this seam (the two cores share no
/// types — that is what keeps the embed core AOT-clean).
/// <para>
/// Retrieval is scoped to the game a query names (<see cref="GameScope"/>), read against the live
/// blueprint list. The scope is an eligibility filter over the corpus, applied BEFORE the top-k, so
/// a scoped query competes only against documents that could be about it.
/// </para>
/// </summary>
internal sealed class RagRetrieval : IRetrieval
{
    /// <summary>How long the blueprint-name vocabulary is reused before being re-read. The set of
    /// installable games changes on the timescale of a blueprint being added, not of a search.</summary>
    private static readonly TimeSpan VocabularyTtl = TimeSpan.FromMinutes(10);

    private readonly IEmbeddingClient _embeddings;
    private readonly RagIndexProvider _index;
    private readonly IServerInventory _inventory;
    private readonly RagOptions _options;
    private readonly ILogger<RagRetrieval> _logger;

    private readonly SemaphoreSlim _vocabularyLock = new(1, 1);
    private IReadOnlyCollection<string> _vocabulary = [];
    private DateTimeOffset _vocabularyExpiry = DateTimeOffset.MinValue;

    public RagRetrieval(
        IEmbeddingClient embeddings,
        RagIndexProvider index,
        IServerInventory inventory,
        IOptions<RagOptions> options,
        ILogger<RagRetrieval> logger)
    {
        _embeddings = embeddings;
        _index = index;
        _inventory = inventory;
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

        var scope = GameScope.Resolve(query, await GetVocabularyAsync(cancellationToken));
        var candidates = index.Chunks;
        if (scope is not null)
        {
            candidates = index.Chunks.Where(c => GameScope.Admits(c.SourcePath, scope)).ToArray();
            _logger.LogDebug(
                "Query scoped to game {Game}: {Eligible} of {Total} chunks eligible.",
                scope, candidates.Count, index.Chunks.Count);
        }

        var hits = VectorSearch.TopK(candidates, queryVector, _options.TopK);

        var results = hits
            .Where(h => h.Score >= _options.MinScore)
            .Select(h => new RetrievedChunk(h.Chunk.SourcePath, h.Chunk.HeaderPath, h.Chunk.Text, h.Score))
            .ToArray();

        return Result.Success<IReadOnlyList<RetrievedChunk>>(results);
    }

    /// <summary>
    /// The installable game names, cached for <see cref="VocabularyTtl"/>. An unavailable inventory
    /// yields an empty vocabulary, which scopes nothing — retrieval stays exactly as wide as it was
    /// rather than failing, so a search never depends on the engine being reachable.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> GetVocabularyAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _vocabularyExpiry)
            return _vocabulary;

        await _vocabularyLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _vocabularyExpiry)
                return _vocabulary;

            try
            {
                _vocabulary = await _inventory.GetBlueprintNamesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read the blueprint list; retrieval runs unscoped.");
                _vocabulary = [];
            }

            _vocabularyExpiry = DateTimeOffset.UtcNow.Add(VocabularyTtl);
            return _vocabulary;
        }
        finally
        {
            _vocabularyLock.Release();
        }
    }
}
