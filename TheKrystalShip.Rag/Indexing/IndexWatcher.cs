using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// The daemon half of the indexer (plan §D6): watch the configured sources and keep the on-disk
/// index current, incrementally. It builds the embed client and <see cref="IndexBuilder"/> once for
/// the process lifetime, does an initial build, then re-indexes whenever a source changes —
/// debounced and coalesced by <see cref="CoalescingRebuildLoop"/> so a burst of editor saves yields
/// one rebuild.
/// <para>
/// The watcher is deliberately dumb: it never tracks <em>which</em> file changed. Every event just
/// pokes the loop, and the rebuild re-enumerates and content-hashes all sources, so the incremental
/// reuse in <see cref="IndexBuilder"/> (D8) decides what is actually re-embedded. That is also why a
/// FileSystemWatcher buffer-overflow (<c>Error</c>) needs nothing special — a single signal triggers
/// a full re-scan that recovers whatever events were dropped.
/// </para>
/// <para>
/// One gap by design: if the embedder is unreachable at startup the initial build fails (logged) but
/// the loop still watches; should the embedder recover with no subsequent file edit, the index stays
/// stale until the next change. There is no periodic retry — re-indexing is change-driven.
/// </para>
/// </summary>
public sealed class IndexWatcher
{
    // A bit above the 8 KiB default: fewer overflow Errors under a bursty save, at trivial cost.
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly RagEmbeddingOptions _embedding;
    private readonly IndexBuilderOptions _build;
    private readonly string _outputPath;
    private readonly TimeSpan _debounce;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IEmbeddingClient? _embeddingsOverride;

    public IndexWatcher(
        RagEmbeddingOptions embedding,
        IndexBuilderOptions build,
        string outputPath,
        TimeSpan debounce,
        ILoggerFactory loggerFactory)
        : this(embeddingsOverride: null, embedding, build, outputPath, debounce, loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(embedding);
    }

    /// <summary>Test seam: inject the embed client directly so the FileSystemWatcher → re-index wiring
    /// can be exercised without a live Ollama (the public ctor builds an <see cref="OllamaEmbeddingClient"/>).</summary>
    internal IndexWatcher(
        IEmbeddingClient embeddings,
        IndexBuilderOptions build,
        string outputPath,
        TimeSpan debounce,
        ILoggerFactory loggerFactory)
        : this(embeddings, new RagEmbeddingOptions(), build, outputPath, debounce, loggerFactory)
    {
    }

    private IndexWatcher(
        IEmbeddingClient? embeddingsOverride,
        RagEmbeddingOptions embedding,
        IndexBuilderOptions build,
        string outputPath,
        TimeSpan debounce,
        ILoggerFactory loggerFactory)
    {
        _embeddingsOverride = embeddingsOverride;
        _embedding = embedding;
        _build = build ?? throw new ArgumentNullException(nameof(build));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _outputPath = outputPath;
        _debounce = debounce > TimeSpan.Zero ? debounce : TimeSpan.FromMilliseconds(750);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<IndexWatcher>();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var embeddings = _embeddingsOverride ?? new OllamaEmbeddingClient(
            Options.Create(_embedding), _loggerFactory.CreateLogger<OllamaEmbeddingClient>());
        var builder = new IndexBuilder(embeddings, _loggerFactory.CreateLogger<IndexBuilder>());

        var loop = new CoalescingRebuildLoop(
            rebuild: token => RebuildAsync(builder, token),
            settle: token => Task.Delay(_debounce, token),
            logger: _logger);

        var watchers = CreateWatchers(loop.Signal);
        try
        {
            if (watchers.Count == 0)
            {
                _logger.LogError("No watchable sources resolved — nothing to watch. Exiting.");
                return;
            }

            _logger.LogInformation(
                "Watching {Count} source location(s) for '{Pattern}' changes (debounce {Debounce} ms); index → {Index}.",
                watchers.Count, _build.SearchPattern, _debounce.TotalMilliseconds, _outputPath);

            // Watchers are live before the initial build is queued, so an edit during that build is
            // captured (it folds into the next pass) rather than slipping through an unwatched window.
            loop.Signal();
            await loop.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var w in watchers)
                w.Dispose();
        }
    }

    private async Task RebuildAsync(IndexBuilder builder, CancellationToken cancellationToken)
    {
        RagIndex? previous = RagIndexFile.TryReadFromFile(_outputPath, out var existing) ? existing : null;

        var result = await builder.BuildAsync(_build, _outputPath, previous, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Re-index failed: {Error}", result.Error);
            return;
        }

        var r = result.Value!;
        _logger.LogInformation(
            "Re-indexed {Files} file(s): {Embedded} embedded, {Reused} reused, {Removed} removed; "
            + "{Chunks} chunks ({New} newly embedded).",
            r.SourceFiles, r.FilesEmbedded, r.FilesReused, r.FilesRemoved, r.TotalChunks, r.ChunksEmbedded);
    }

    /// <summary>One <see cref="FileSystemWatcher"/> per distinct source location: a directory source
    /// is watched recursively filtered to the pattern; a file source watches its parent directory
    /// filtered to that file's name. Missing sources are skipped (matching the builder).</summary>
    private List<FileSystemWatcher> CreateWatchers(Action onChange)
    {
        var watchers = new List<FileSystemWatcher>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in _build.Sources)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var full = Path.GetFullPath(source);

            string directory, filter;
            bool recursive;
            if (Directory.Exists(full))
            {
                directory = full;
                filter = string.IsNullOrWhiteSpace(_build.SearchPattern) ? "*.md" : _build.SearchPattern;
                recursive = true;
            }
            else if (File.Exists(full))
            {
                directory = Path.GetDirectoryName(full) ?? full;
                filter = Path.GetFileName(full);
                recursive = false;
            }
            else
            {
                _logger.LogWarning("Source not found, not watching: {Source}", source);
                continue;
            }

            // Collapse overlapping sources so one edit doesn't fan out into duplicate signals.
            if (!seen.Add($"{directory}|{filter}|{recursive}"))
                continue;

            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(directory, filter)
                {
                    IncludeSubdirectories = recursive,
                    InternalBufferSize = WatcherBufferBytes,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not watch source {Source}; skipping it.", source);
                continue;
            }

            watcher.Created += (_, _) => onChange();
            watcher.Changed += (_, _) => onChange();
            watcher.Deleted += (_, _) => onChange();
            watcher.Renamed += (_, _) => onChange();
            watcher.Error += (_, e) =>
            {
                // Buffer overflow drops events; a single signal forces a full re-scan that recovers them.
                _logger.LogWarning(e.GetException(), "File watcher error on {Directory}; forcing a re-index.", directory);
                onChange();
            };
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }

        return watchers;
    }
}
