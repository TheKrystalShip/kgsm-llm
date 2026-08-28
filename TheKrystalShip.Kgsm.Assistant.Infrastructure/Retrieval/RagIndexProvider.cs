using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;

/// <summary>
/// Loads the on-disk RAG index and caches it (reader idiom: load fully, then close).
/// The index is a regenerable artifact written atomically by the standalone indexer ;
/// this provider only ever reads it.
/// <para>
/// Lazy-loads on first use and caches the load, re-attempting on every call while the file is still
/// absent (the indexer may not have produced it yet — an expected state, not an error). Phase 3b adds
/// hot-reload: every <see cref="Get"/> cheaply stats the file (last-write + length) and, when the
/// indexer atomically swaps in a new build, reloads it. A reload that fails (a mid-swap read, a
/// corrupt or model-mismatched new build) <em>degrades to the last good index</em> rather than going
/// dark; the observed stamp is advanced on every attempt — success or failure — so a bad swap-in is
/// read once, not re-read on every query, while a subsequent good build still self-heals.
/// </para>
/// <para>
/// Enforces the decoupling contract between the two independently-deployed binaries: a stamped
/// <see cref="RagIndex.EmbeddingModel"/> that disagrees with the configured embedder means a
/// different vector space, so the vectors are meaningless — reject the index (rebuild, never
/// migrate). Never throws; every unavailable state is a failed <see cref="Result"/>.
/// </para>
/// </summary>
public sealed class RagIndexProvider
{
    private readonly RagOptions _options;
    private readonly string _expectedModel;
    private readonly ILogger<RagIndexProvider> _logger;
    private readonly object _gate = new();
    private RagIndex? _cached;

    // The on-disk identity of the version behind <see cref="_cached"/> (or the last one we attempted
    // to load). null = no file. Advanced on every load attempt so each distinct version is read once.
    private (DateTime WriteUtc, long Length)? _stamp;

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
    /// <summary>
    /// Why this is answering from a previously-loaded index rather than what is on disk, or
    /// <see langword="null"/> when the two agree.
    /// </summary>
    /// <remarks>
    /// <b>The state a success cannot express.</b> When a reload fails, <see cref="Get"/> deliberately
    /// keeps serving the last good index rather than going dark — so it returns success while answering
    /// from something that is no longer what the indexer wrote. That is working, and not with the
    /// current corpus, which no caller can tell from the result alone. Retrieval reads this and does not
    /// care; a leaf reporting its own health does.
    /// </remarks>
    public string? ServingLastGoodBecause
    {
        get { lock (_gate) { return _staleReason; } }
    }

    private string? _staleReason;

    public Result<RagIndex> Get()
    {
        lock (_gate)
        {
            var current = TryStamp();

            // Fast path: we have a cached index and the file on disk is the same version we loaded.
            if (_cached is not null && current == _stamp)
                return Result.Success(_cached);

            // First load, or the indexer swapped in a new build. Record the observation NOW — before
            // the load can fail — so a bad swap-in is read once, not re-read on every subsequent call.
            _stamp = current;

            var loaded = Load();
            if (loaded.IsSuccess)
            {
                _staleReason = null;
                return loaded;
            }

            // The new on-disk version is unreadable (mid-swap, corrupt, model-mismatched). If we have
            // a previously-loaded index, keep serving it rather than going dark; a later good build,
            // having a new stamp, will be picked up and replace it.
            if (_cached is not null)
            {
                _logger.LogWarning(
                    "Reload of the changed retrieval index failed ({Error}); continuing with the previously loaded index.",
                    loaded.Error);
                _staleReason = loaded.Error;
                return Result.Success(_cached);
            }

            return loaded;
        }
    }

    /// <summary>The on-disk identity of the index file (last-write + length), or null when it is
    /// absent or unreadable. Cheap (one stat) and never throws — a missing file is just "no version".</summary>
    private (DateTime WriteUtc, long Length)? TryStamp()
    {
        if (string.IsNullOrWhiteSpace(_options.IndexPath))
            return null;

        try
        {
            var info = new FileInfo(_options.IndexPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

        // A different embedding model means a different vector space — reject, don't query it.
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
