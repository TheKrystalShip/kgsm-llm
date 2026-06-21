namespace TheKrystalShip.Rag.Indexer;

/// <summary>
/// Parsed command line for the standalone indexer. Two modes: <c>--once</c> (build and exit) and
/// <c>--watch</c> (the Phase 3b daemon — re-index on source changes until signalled to stop). Value
/// flags accept both <c>--flag value</c> and <c>--flag=value</c>; <c>--source</c> repeats.
/// </summary>
internal sealed record IndexerArgs
{
    public List<string> Sources { get; init; } = [];
    public string? IndexPath { get; init; }
    public string Model { get; init; } = "embeddinggemma";
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Pattern { get; init; } = "*.md";
    public int ChunkSize { get; init; } = 2000;
    public int ChunkOverlap { get; init; } = 200;
    public int TimeoutSeconds { get; init; } = 120;
    public int DebounceMs { get; init; } = 750;
    public bool Once { get; init; }
    public bool Watch { get; init; }
    public bool Full { get; init; }
    public bool Verbose { get; init; }
    public bool Help { get; init; }

    public static bool TryParse(string[] args, out IndexerArgs options, out string? error)
    {
        var sources = new List<string>();
        string? indexPath = null;
        string model = "embeddinggemma", endpoint = "http://localhost:11434", pattern = "*.md";
        int chunkSize = 2000, chunkOverlap = 200, timeout = 120, debounceMs = 750;
        bool once = false, watch = false, full = false, verbose = false, help = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var (name, inlineValue) = Split(args[i]);
            switch (name)
            {
                case "--once": once = true; break;
                case "--watch": watch = true; break;
                case "--full": full = true; break;
                case "--verbose": verbose = true; break;
                case "--help" or "-h": help = true; break;

                case "--source":
                    if (!Value(args, ref i, inlineValue, name, out var src, out error)) { options = Empty; return false; }
                    sources.Add(src!);
                    break;
                case "--index":
                    if (!Value(args, ref i, inlineValue, name, out indexPath, out error)) { options = Empty; return false; }
                    break;
                case "--model":
                    if (!Value(args, ref i, inlineValue, name, out model!, out error)) { options = Empty; return false; }
                    break;
                case "--endpoint":
                    if (!Value(args, ref i, inlineValue, name, out endpoint!, out error)) { options = Empty; return false; }
                    break;
                case "--pattern":
                    if (!Value(args, ref i, inlineValue, name, out pattern!, out error)) { options = Empty; return false; }
                    break;
                case "--chunk-size":
                    if (!IntValue(args, ref i, inlineValue, name, out chunkSize, out error)) { options = Empty; return false; }
                    break;
                case "--chunk-overlap":
                    if (!IntValue(args, ref i, inlineValue, name, out chunkOverlap, out error)) { options = Empty; return false; }
                    break;
                case "--timeout":
                    if (!IntValue(args, ref i, inlineValue, name, out timeout, out error)) { options = Empty; return false; }
                    break;
                case "--debounce-ms":
                    if (!IntValue(args, ref i, inlineValue, name, out debounceMs, out error)) { options = Empty; return false; }
                    break;

                default:
                    error = $"unknown option '{args[i]}'";
                    options = Empty;
                    return false;
            }
        }

        options = new IndexerArgs
        {
            Sources = sources,
            IndexPath = indexPath,
            Model = model,
            Endpoint = endpoint,
            Pattern = pattern,
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap,
            TimeoutSeconds = timeout,
            DebounceMs = debounceMs,
            Once = once,
            Watch = watch,
            Full = full,
            Verbose = verbose,
            Help = help,
        };
        return true;
    }

    private static (string Name, string? InlineValue) Split(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }

    private static bool Value(string[] args, ref int i, string? inline, string name, out string? value, out string? error)
    {
        if (inline is not null) { value = inline; error = null; return true; }
        if (i + 1 >= args.Length) { value = null; error = $"option '{name}' requires a value"; return false; }
        value = args[++i];
        error = null;
        return true;
    }

    private static bool IntValue(string[] args, ref int i, string? inline, string name, out int value, out string? error)
    {
        if (!Value(args, ref i, inline, name, out var raw, out error)) { value = 0; return false; }
        if (!int.TryParse(raw, out value)) { error = $"option '{name}' expects an integer, got '{raw}'"; return false; }
        return true;
    }

    private static IndexerArgs Empty => new();

    public const string Usage =
        """
        kgsm-rag-indexer — build the KGSM RAG index from a docs corpus

        USAGE:
          kgsm-rag-indexer --once  --source <path> [--source <path>...] --index <file> [options]
          kgsm-rag-indexer --watch --source <path> [--source <path>...] --index <file> [options]

        MODE (pick one):
          --once               build the index once and exit
          --watch              daemon: build once, then re-index on source changes until stopped
                               (SIGINT/SIGTERM); intended to run under systemd

        REQUIRED:
          --source <path>      a file or directory to index (repeatable; dirs walked recursively)
          --index <file>       output index path (its directory must share a filesystem for the atomic swap)

        OPTIONS:
          --model <tag>        embedding model (default: embeddinggemma)
          --endpoint <url>     Ollama base URL (default: http://localhost:11434)
          --pattern <glob>     file glob when walking a directory (default: *.md)
          --chunk-size <n>     chunk target size in characters (default: 2000)
          --chunk-overlap <n>  chunk overlap in characters (default: 200)
          --timeout <s>        embed request timeout in seconds (default: 120)
          --debounce-ms <n>    (--watch) coalesce a burst of changes over this window (default: 750)
          --full               ignore any existing index and rebuild from scratch (--once only)
          --verbose            show debug logs on stderr
          -h, --help           show this help and exit

        Incremental by default: an existing index at --index is reused for files whose content is
        unchanged (a different model/dimension/chunk-size triggers a full rebuild automatically).
        """;
}
