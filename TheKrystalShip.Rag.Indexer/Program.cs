using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using TheKrystalShip.Rag.Indexer;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Ollama;

// Exit codes: 0 ok · 1 runtime failure · 2 usage error · 130 cancelled (SIGINT).
const int ExitOk = 0, ExitRuntime = 1, ExitUsage = 2, ExitCancelled = 130;

if (!IndexerArgs.TryParse(args, out var opts, out var parseError))
{
    Console.Error.WriteLine($"kgsm-rag-indexer: {parseError}");
    Console.Error.WriteLine("Try 'kgsm-rag-indexer --help'.");
    return ExitUsage;
}

if (opts.Help)
{
    Console.Out.Write(IndexerArgs.Usage);
    return ExitOk;
}

if (opts.Watch)
{
    Console.Error.WriteLine("kgsm-rag-indexer: --watch (daemon mode) is not implemented yet (Phase 3b). Use --once.");
    return ExitUsage;
}

if (!opts.Once)
{
    Console.Error.WriteLine("kgsm-rag-indexer: specify --once.");
    Console.Error.WriteLine("Try 'kgsm-rag-indexer --help'.");
    return ExitUsage;
}

if (opts.Sources.Count == 0)
{
    Console.Error.WriteLine("kgsm-rag-indexer: at least one --source is required.");
    return ExitUsage;
}

if (string.IsNullOrWhiteSpace(opts.IndexPath))
{
    Console.Error.WriteLine("kgsm-rag-indexer: --index <file> is required.");
    return ExitUsage;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(opts.Verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddSimpleConsole();
    // Everything to stderr — stdout stays clean for any future machine-readable output.
    builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
});
var logger = loggerFactory.CreateLogger("kgsm-rag-indexer");

// Ctrl-C cancels an in-flight build (embedding a large corpus can be long) rather than killing hard.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var embedding = new RagEmbeddingOptions
{
    Endpoint = opts.Endpoint,
    EmbeddingModel = opts.Model,
    TimeoutSeconds = opts.TimeoutSeconds,
};
var build = new IndexBuilderOptions
{
    Sources = opts.Sources,
    SearchPattern = opts.Pattern,
    ChunkSize = opts.ChunkSize,
    ChunkOverlap = opts.ChunkOverlap,
};

try
{
    var result = await IndexRunner.RunAsync(embedding, build, opts.IndexPath!, opts.Full, loggerFactory, cts.Token);
    if (result.IsFailure)
    {
        logger.LogError("Indexing failed: {Error}", result.Error);
        return ExitRuntime;
    }

    var r = result.Value!;
    logger.LogInformation(
        "Indexed {Files} file(s) → {Index}: {Embedded} embedded, {Reused} reused, {Removed} removed; "
        + "{Chunks} chunks ({ChunksEmbedded} newly embedded).",
        r.SourceFiles, opts.IndexPath, r.FilesEmbedded, r.FilesReused, r.FilesRemoved, r.TotalChunks, r.ChunksEmbedded);
    return ExitOk;
}
catch (OperationCanceledException)
{
    logger.LogWarning("Indexing cancelled.");
    return ExitCancelled;
}
