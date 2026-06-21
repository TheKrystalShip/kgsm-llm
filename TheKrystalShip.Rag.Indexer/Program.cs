using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using TheKrystalShip.Rag.Indexer;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Ollama;

// Exit codes: 0 ok · 1 runtime failure · 2 usage error · 130 cancelled (one-shot SIGINT). A --watch
// daemon stopping on a signal is its intended end-of-life, so it exits 0 (not 130) — that keeps a
// systemd `Restart=on-failure` from re-spawning an intentional `systemctl stop`.
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

if (opts.Once == opts.Watch) // neither, or both
{
    Console.Error.WriteLine("kgsm-rag-indexer: specify exactly one of --once or --watch.");
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
    if (opts.Watch)
    {
        // Daemon mode: the journald-native sink (the <N> syslog priority prefix lets `journalctl -p`
        // filter by level) — the ecosystem convention for a service, matching the watchdog/monitor.
        builder.AddSystemdConsole();
    }
    else
    {
        // Interactive one-shot: simple lines, everything to stderr so stdout stays clean for any
        // future machine-readable output (the convention's "CLI variant").
        builder.AddSimpleConsole();
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
});
var logger = loggerFactory.CreateLogger("kgsm-rag-indexer");

// Cancel an in-flight build / stop the daemon gracefully on a signal rather than a hard kill.
// SIGINT covers Ctrl-C; SIGTERM is what `systemctl stop` sends — without it the daemon would be
// hard-killed after the stop timeout. PosixSignalRegistration is AOT-safe; keep both in `using`
// scope so they aren't collected while the daemon runs.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

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

if (opts.Watch)
{
    if (opts.Full)
        logger.LogWarning("--full is ignored in --watch mode (the daemon always re-indexes incrementally).");

    try
    {
        var watcher = new IndexWatcher(
            embedding, build, opts.IndexPath!, TimeSpan.FromMilliseconds(opts.DebounceMs), loggerFactory);
        await watcher.RunAsync(cts.Token);
        return ExitOk;
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("Watcher stopped.");
        return ExitOk;
    }
}

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
