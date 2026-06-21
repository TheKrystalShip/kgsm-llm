using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// The <c>kgsm-assistant index</c> verb: builds the RAG index from the operator's <c>Rag</c> config
/// via the shared core engine (<see cref="IndexRunner"/>). Deliberately independent of KGSM and the
/// chat backend — it never resolves <c>IServerAssistant</c> or the kgsm-lib graph, so a box with
/// Ollama but no kgsm can still index. The standalone <c>kgsm-rag-indexer</c> binary is the
/// daemon-grade alternative (and the only one with <c>--watch</c>); this verb is the "already have
/// the CLI, index without shipping a second binary" convenience (plan §3.1).
/// </summary>
internal static class IndexCommand
{
    public static async Task<int> RunAsync(IConfiguration config, string[] verbArgs)
    {
        const int ExitOk = 0, ExitRuntime = 1, ExitUsage = 2, ExitCancelled = 130;

        if (!IndexVerbArgs.TryParse(verbArgs, out var a, out var error))
        {
            Console.Error.WriteLine($"kgsm-assistant index: {error}");
            Console.Error.WriteLine("Try 'kgsm-assistant index --help'.");
            return ExitUsage;
        }

        if (a.Help)
        {
            Console.Out.Write(IndexVerbArgs.Usage);
            return ExitOk;
        }

        // Both halves of the "Rag" block: the host half (paths, chunking) + the embedder half.
        var rag = config.GetSection(RagOptions.Section).Get<RagOptions>() ?? new RagOptions();
        var embedding = config.GetSection(RagEmbeddingOptions.Section).Get<RagEmbeddingOptions>()
            ?? new RagEmbeddingOptions();

        if (string.IsNullOrWhiteSpace(rag.IndexPath))
        {
            Console.Error.WriteLine("kgsm-assistant index: Rag:IndexPath is not configured.");
            return ExitUsage;
        }

        var sources = a.Sources.Count > 0 ? [.. a.Sources] : rag.Sources;
        if (sources.Length == 0)
        {
            Console.Error.WriteLine(
                "kgsm-assistant index: no sources to index — set Rag:Sources or pass --source <path>.");
            return ExitUsage;
        }

        var noColor = a.NoColor || Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(a.Verbose ? LogLevel.Debug : LogLevel.Information);
            b.AddSimpleConsole(o => o.ColorBehavior = noColor ? LoggerColorBehavior.Disabled : LoggerColorBehavior.Default);
            b.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
        var logger = loggerFactory.CreateLogger("kgsm-assistant index");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var build = new IndexBuilderOptions
        {
            Sources = sources,
            SearchPattern = string.IsNullOrWhiteSpace(rag.SourcePattern) ? "*.md" : rag.SourcePattern,
            ChunkSize = rag.ChunkSize,
            ChunkOverlap = rag.ChunkOverlap,
        };

        try
        {
            var result = await IndexRunner.RunAsync(embedding, build, rag.IndexPath, a.Full, loggerFactory, cts.Token);
            if (result.IsFailure)
            {
                logger.LogError("Indexing failed: {Error}", result.Error);
                return ExitRuntime;
            }

            var r = result.Value!;
            logger.LogInformation(
                "Indexed {Files} file(s) → {Index}: {Embedded} embedded, {Reused} reused, {Removed} removed; "
                + "{Chunks} chunks ({New} newly embedded).",
                r.SourceFiles, rag.IndexPath, r.FilesEmbedded, r.FilesReused, r.FilesRemoved, r.TotalChunks, r.ChunksEmbedded);
            return ExitOk;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Indexing cancelled.");
            return ExitCancelled;
        }
    }
}

/// <summary>
/// Sub-flags for the <c>index</c> verb. <c>--config</c> is consumed earlier (it selects the config
/// file before resolution) so it is accepted-and-skipped here rather than rejected as unknown.
/// </summary>
internal sealed record IndexVerbArgs
{
    public List<string> Sources { get; init; } = [];
    public bool Full { get; init; }
    public bool Verbose { get; init; }
    public bool NoColor { get; init; }
    public bool Help { get; init; }

    public static bool TryParse(string[] args, out IndexVerbArgs options, out string? error)
    {
        var sources = new List<string>();
        bool full = false, verbose = false, noColor = false, help = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--full": full = true; break;
                case "--verbose": verbose = true; break;
                case "--no-color": noColor = true; break;
                case "--help" or "-h": help = true; break;
                case "--config": // selects the config file (handled before resolution); skip its value
                    if (i + 1 < args.Length) i++;
                    break;
                case "--source":
                    if (i + 1 >= args.Length) { error = "option '--source' requires a value"; options = Empty; return false; }
                    sources.Add(args[++i]);
                    break;
                default:
                    if (arg.StartsWith("--config=", StringComparison.Ordinal))
                        break; // handled before resolution
                    if (arg.StartsWith("--source=", StringComparison.Ordinal))
                    {
                        sources.Add(arg["--source=".Length..]);
                        break;
                    }
                    error = $"unknown option '{arg}'";
                    options = Empty;
                    return false;
            }
        }

        options = new IndexVerbArgs { Sources = sources, Full = full, Verbose = verbose, NoColor = noColor, Help = help };
        return true;
    }

    private static IndexVerbArgs Empty => new();

    public const string Usage =
        """
        kgsm-assistant index — build the RAG index from your configured docs

        USAGE:
          kgsm-assistant index [options]

        Reads the `Rag` config block (IndexPath, Sources, EmbeddingModel, ChunkSize, …). For a
        standalone / daemon indexer (and --watch), use the kgsm-rag-indexer binary instead.

        OPTIONS:
          --source <path>    add a source (repeatable); overrides Rag:Sources when given
          --full             ignore any existing index and rebuild from scratch
          --config <path>    use this config file (same as the top-level --config)
          --no-color         disable colored log output (also honored: NO_COLOR)
          --verbose          show debug logs on stderr
          -h, --help         show this help and exit

        Incremental by default: unchanged files are reused; a changed model/dimension/chunk-size
        triggers a full rebuild automatically.
        """;
}
