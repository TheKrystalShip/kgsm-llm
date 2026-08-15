using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// Thin composition for hosts: construct the embed client, (unless a full rebuild is forced) load
/// the previous index for incremental reuse, and run the build. Both indexer hosts — the standalone
/// AOT binary and the assistant CLI's <c>index</c> verb — call this so the wiring lives in one
/// AOT-safe place; each host owns only its own arg/config parsing and output formatting.
/// </summary>
public static class IndexRunner
{
    public static Task<Result<IndexBuildReport>> RunAsync(
        RagEmbeddingOptions embedding,
        IndexBuilderOptions build,
        string outputPath,
        bool fullRebuild,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var embeddings = EmbeddingClientFactory.Create(embedding, loggerFactory);
        var builder = new IndexBuilder(embeddings, loggerFactory.CreateLogger<IndexBuilder>());

        RagIndex? previous = null;
        if (!fullRebuild && RagIndexFile.TryReadFromFile(outputPath, out var existing))
            previous = existing;

        return builder.BuildAsync(build, outputPath, previous, cancellationToken);
    }
}
