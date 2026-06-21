using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;

/// <summary>
/// Loads the on-disk RAG index and caches it (plan §D8 reader idiom: load fully, then close).
/// The index is a regenerable artifact written atomically by the standalone indexer (plan §D6);
/// this provider only ever reads it.
/// <para>
/// Phase 2 lazy-loads on first use and caches the FIRST successful load, re-attempting on every
/// call while the file is still absent (the indexer may not have produced it yet — an expected
/// state, not an error). Hot-reload on atomic swap is Phase 3b: it drops in at the marked spot in
/// <see cref="Get"/> as a freshness check before returning the cache.
/// </para>
/// <para>
/// Enforces the §D9 decoupling contract between the two independently-deployed binaries: a stamped
/// <see cref="RagIndex.EmbeddingModel"/> that disagrees with the configured embedder means a
/// different vector space, so the vectors are meaningless — reject the index (rebuild, never
/// migrate). Never throws; every unavailable state is a failed <see cref="Result"/>.
/// </para>
/// </summary>
internal sealed class RagIndexProvider
{
    private readonly RagOptions _options;
    private readonly string _expectedModel;
    private readonly ILogger<RagIndexProvider> _logger;
    private readonly object _gate = new();
    private RagIndex? _cached;

    public RagIndexProvider(
        IOptions<RagOptions> options,
        IEmbeddingClient embeddings,
        ILogger<RagIndexProvider> logger)
    {
        _options = options.Value;
        _expectedModel = embeddings.ModelName;
        _logger = logger;
    }

    /// <summary>
    /// Returns the loaded index, or a failure describing why it is unavailable (no path configured,
    /// the file is missing/unreadable, a format/version mismatch, or an embedding-model mismatch).
    /// </summary>
    public Result<RagIndex> Get()
    {
        lock (_gate)
        {
            // Phase 3b hot-reload hooks in here: `if (_cached is not null && !FileChanged()) ...`.
            if (_cached is not null)
                return Result.Success(_cached);

            return Load();
        }
    }

    private Result<RagIndex> Load()
    {
        if (string.IsNullOrWhiteSpace(_options.IndexPath))
            return Result.Failure<RagIndex>("no retrieval index path is configured");

        RagIndex index;
        try
        {
            index = RagIndexFile.ReadFromFile(_options.IndexPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Expected before the indexer's first run — not an error.
            _logger.LogDebug(
                "Retrieval index not found at {Path}; unavailable until the indexer builds it.", _options.IndexPath);
            return Result.Failure<RagIndex>("the retrieval index has not been built yet");
        }
        catch (RagIndexFormatException ex)
        {
            // Bad magic / unsupported version: the artifact is stale or foreign — the indexer must rebuild it.
            _logger.LogWarning(ex, "Retrieval index at {Path} is not readable by this build.", _options.IndexPath);
            return Result.Failure<RagIndex>("the retrieval index is from an incompatible build and must be rebuilt");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the retrieval index at {Path}.", _options.IndexPath);
            return Result.Failure<RagIndex>("the retrieval index could not be read");
        }
        catch (Exception ex)
        {
            // Backstop for the never-throw contract: a file with valid magic+version but a corrupt
            // body can throw outside the IO family (e.g. FormatException from a malformed 7-bit
            // string-length prefix). The index is a regenerable artifact, so any unexpected read
            // error means "unavailable, rebuild" — never a thrown exception out of retrieval.
            _logger.LogWarning(ex, "Retrieval index at {Path} is corrupt and could not be parsed.", _options.IndexPath);
            return Result.Failure<RagIndex>("the retrieval index is corrupt and must be rebuilt");
        }

        // §D9: a different embedding model means a different vector space — reject, don't query it.
        if (!string.Equals(index.EmbeddingModel, _expectedModel, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Retrieval index was built with model '{Indexed}' but the configured embedder is '{Configured}'; "
                + "ignoring it until it is rebuilt.", index.EmbeddingModel, _expectedModel);
            return Result.Failure<RagIndex>(
                $"the retrieval index was built with a different embedding model ('{index.EmbeddingModel}') and must be rebuilt");
        }

        _cached = index;
        _logger.LogInformation(
            "Loaded retrieval index: {Chunks} chunks, model '{Model}', dimension {Dim}.",
            index.Chunks.Count, index.EmbeddingModel, index.Dimension);
        return Result.Success(index);
    }
}
