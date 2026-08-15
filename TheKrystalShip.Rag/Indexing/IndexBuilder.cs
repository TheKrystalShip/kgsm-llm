using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Rag.Chunking;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// Builds a <see cref="RagIndex"/> from source files: walk sources → chunk (structure-aware) →
/// embed → assemble → atomic write. Incremental (D8): when handed the previous index, files whose
/// bytes are unchanged keep their existing chunks+vectors instead of being re-embedded. Returns a
/// <see cref="Result{T}"/> on every expected failure (bad params, unreadable sources, embedder
/// down/inconsistent, write error); cancellation surfaces as <see cref="OperationCanceledException"/>
/// for the host to map to its cancelled exit code.
/// </summary>
public sealed class IndexBuilder
{
    // Probes the embedder once per build: confirms it is reachable (fail fast, before chunking) and
    // reveals the current vector-space dimension so stale-dimension reuse can be rejected up front.
    private const string DimensionProbe = "kgsm-rag index dimension probe";

    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger<IndexBuilder> _logger;

    public IndexBuilder(IEmbeddingClient embeddings, ILogger<IndexBuilder>? logger = null)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _logger = logger ?? NullLogger<IndexBuilder>.Instance;
    }

    public async Task<Result<IndexBuildReport>> BuildAsync(
        IndexBuilderOptions options, string outputPath, RagIndex? previous = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        MarkdownChunker chunker;
        try
        {
            chunker = new MarkdownChunker(new ChunkingOptions
            {
                ChunkSize = options.ChunkSize,
                ChunkOverlap = options.ChunkOverlap,
            });
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<IndexBuildReport>(ex.Message);
        }

        List<string> files;
        try
        {
            files = EnumerateSourceFiles(options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not enumerate source files.");
            return Result.Failure<IndexBuildReport>("could not read the source directories");
        }

        if (files.Count == 0)
            return Result.Failure<IndexBuildReport>("no source files matched the configured sources/pattern");

        // Probe: reachable embedder + current dimension (fail fast before any file work).
        var probe = await _embeddings.EmbedDocumentsAsync([DimensionProbe], cancellationToken);
        if (probe.IsFailure)
            return Result.Failure<IndexBuildReport>(probe.Error!);
        if (probe.Value!.Count == 0 || probe.Value[0].Length == 0)
            return Result.Failure<IndexBuildReport>("the embedder returned no probe vector");
        var dimension = probe.Value[0].Length;

        // Incremental reuse is valid only when the previous index shares this run's whole shape:
        // same vector space (model + dimension) AND the same chunking parameters — otherwise the
        // carried-over chunks would be split (or embedded) differently than the new header claims.
        // The byte-level content hash then decides per-file freshness within that envelope.
        var reuse = BuildReuseLookup(previous, options, dimension);

        var chunks = new List<IndexedChunk>();
        var manifest = new List<SourceFileEntry>(files.Count);
        int filesEmbedded = 0, filesReused = 0, chunksEmbedded = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not read source file {File}.", file);
                return Result.Failure<IndexBuildReport>($"could not read source file '{file}'");
            }

            var hash = Sha256Hex(bytes);
            var offset = chunks.Count;

            if (reuse is not null && reuse.TryGetValue(file, out var prevEntry) && prevEntry.ContentHash == hash)
            {
                // Unchanged file — carry its chunks (with their vectors) over verbatim.
                for (var i = 0; i < prevEntry.ChunkCount; i++)
                    chunks.Add(previous!.Chunks[prevEntry.ChunkOffset + i]);
                manifest.Add(new SourceFileEntry(file, hash, offset, prevEntry.ChunkCount));
                filesReused++;
                continue;
            }

            var fileChunks = chunker.Chunk(Encoding.UTF8.GetString(bytes), file);
            if (fileChunks.Count > 0)
            {
                var inputs = fileChunks.Select(c => c.Text).ToArray();
                var embedded = await _embeddings.EmbedDocumentsAsync(inputs, cancellationToken);
                if (embedded.IsFailure)
                    return Result.Failure<IndexBuildReport>(embedded.Error!);

                var vectors = embedded.Value!;
                if (vectors.Count != fileChunks.Count)
                    return Result.Failure<IndexBuildReport>("the embedder returned the wrong number of vectors");

                for (var i = 0; i < fileChunks.Count; i++)
                {
                    if (vectors[i].Length != dimension)
                        return Result.Failure<IndexBuildReport>("the embedder returned an inconsistent vector dimension");
                    var c = fileChunks[i];
                    chunks.Add(new IndexedChunk(c.SourcePath, c.HeaderPath, c.Text, vectors[i]));
                }
                chunksEmbedded += fileChunks.Count;
            }

            manifest.Add(new SourceFileEntry(file, hash, offset, fileChunks.Count));
            filesEmbedded++;
        }

        var present = new HashSet<string>(files, StringComparer.Ordinal);
        var filesRemoved = previous?.Manifest.Count(m => !present.Contains(m.Path)) ?? 0;

        if (chunks.Count == 0)
            _logger.LogWarning("Index built with 0 chunks — the sources matched files but produced no content.");

        var index = new RagIndex
        {
            EmbeddingModel = _embeddings.ModelName,
            Dimension = dimension,
            ChunkSize = options.ChunkSize,
            ChunkOverlap = options.ChunkOverlap,
            Chunks = chunks,
            Manifest = manifest,
        };

        try
        {
            RagIndexFile.WriteToFile(outputPath, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RagIndexFormatException)
        {
            _logger.LogError(ex, "Could not write the index to {Path}.", outputPath);
            return Result.Failure<IndexBuildReport>($"could not write the index to '{outputPath}'");
        }

        return Result.Success(new IndexBuildReport(
            files.Count, filesEmbedded, filesReused, filesRemoved, chunks.Count, chunksEmbedded));
    }

    /// <summary>Per-file manifest of a previous index, keyed by source path — but only when that index
    /// is reuse-compatible with this run (model + dimension + chunking). Null disables all reuse.</summary>
    private Dictionary<string, SourceFileEntry>? BuildReuseLookup(
        RagIndex? previous, IndexBuilderOptions options, int dimension)
    {
        if (previous is null)
            return null;

        var compatible =
            string.Equals(previous.EmbeddingModel, _embeddings.ModelName, StringComparison.Ordinal)
            && previous.Dimension == dimension
            && previous.ChunkSize == options.ChunkSize
            && previous.ChunkOverlap == options.ChunkOverlap;

        if (!compatible)
        {
            _logger.LogInformation(
                "Previous index is not reuse-compatible (model/dimension/chunking changed) — full rebuild.");
            return null;
        }

        var lookup = new Dictionary<string, SourceFileEntry>(previous.Manifest.Count, StringComparer.Ordinal);
        foreach (var entry in previous.Manifest)
            lookup[entry.Path] = entry;
        return lookup;
    }

    /// <summary>Resolves sources to a deterministic, de-duplicated, sorted list of absolute file paths
    /// (stable order keeps the manifest's contiguous chunk slices reproducible).</summary>
    private List<string> EnumerateSourceFiles(IndexBuilderOptions options)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var full = Path.GetFullPath(source);
            if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, options.SearchPattern, SearchOption.AllDirectories))
                    set.Add(Path.GetFullPath(file));
            }
            else if (File.Exists(full))
            {
                set.Add(full);
            }
            else
            {
                _logger.LogWarning("Source not found, skipping: {Source}", source);
            }
        }

        var list = set.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
