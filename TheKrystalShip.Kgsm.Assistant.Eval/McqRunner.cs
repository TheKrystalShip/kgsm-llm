using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>What one MCQ run needs — derived from the CLI by <c>Program</c>, kept separate from
/// <see cref="EvalOptions"/> so the runner has no opinion on arg parsing.</summary>
internal sealed record McqRunConfig(
    string Model,
    string? Endpoint,
    double Temperature,
    int? Seed,
    int Reps,
    string? EmbeddingModel,
    string CorpusDir,
    string McqFile,
    IReadOnlyList<McqCondition> Conditions,
    RagTuning Tuning,
    string? SweepKnob,
    bool Verbose);

/// <summary>
/// Runs the ground-truth MCQ benchmark: for each question it asks the model under each requested
/// condition (closed-book / with-RAG / oracle) and scores whether the chosen letter is CORRECT — the
/// measurement that reproduces the no-RAG → with-RAG → oracle lift chart for our corpus (plan §7).
/// <para>
/// Unlike the routing <see cref="Harness"/> this needs NO kgsm host and never touches the agent loop:
/// it calls <see cref="ILlmClient"/> directly (no tools, so there is no path to any action) and, for
/// with-RAG, builds the index in-process via the RAG core and drives the REAL <see cref="SearchAggregator"/>
/// over it — so the chunk/TopK/MinScore/MaxContextChars values a sweep picks transfer to production.
/// </para>
/// </summary>
internal sealed class McqRunner : IDisposable
{
    // The model may reason, but must commit on a final "Answer: X" line — the parser reads that last marker.
    private const string SystemPrompt =
        "You are taking a multiple-choice exam about the KGSM game-server-management ecosystem and its " +
        "internal design documents. Pick the single best answer. You may think briefly first, but you MUST " +
        "end your reply with a line in exactly this form:\nAnswer: X\nwhere X is one of the choice letters.";

    private readonly ServiceProvider _provider;
    private readonly ILlmClient _llm;
    private readonly IEmbeddingClient _embeddings;
    private readonly IndexBuilder _builder;
    private readonly ILogger<SearchAggregator> _aggregatorLogger;
    private readonly ILogger<McqRunner> _logger;
    private readonly McqRunConfig _config;
    private readonly Dictionary<(int ChunkSize, int ChunkOverlap), RagIndex> _indexCache = new();
    private readonly string _tempDir;

    private McqRunner(ServiceProvider provider, McqRunConfig config)
    {
        _provider = provider;
        _config = config;
        _llm = provider.GetRequiredService<ILlmClient>();
        _embeddings = provider.GetRequiredService<IEmbeddingClient>();
        _builder = provider.GetRequiredService<IndexBuilder>();
        var loggers = provider.GetRequiredService<ILoggerFactory>();
        _aggregatorLogger = loggers.CreateLogger<SearchAggregator>();
        _logger = loggers.CreateLogger<McqRunner>();
        _tempDir = Path.Combine(Path.GetTempPath(), "kgsm-mcq-eval");
        Directory.CreateDirectory(_tempDir);
    }

    public static McqRunner Build(McqRunConfig config)
    {
        var configuration = BuildConfiguration(config);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(config.Verbose ? LogLevel.Debug : LogLevel.Warning);
            b.AddSimpleConsole();
            // stdout is reserved for the scorecard; route backend log lines to stderr.
            b.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        services.AddLocalLlm(configuration);  // ILlmClient (Ollama chat) — no kgsm, no dispatcher
        services.Configure<RagEmbeddingOptions>(configuration.GetSection(RagEmbeddingOptions.Section));
        services.AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>();
        services.AddSingleton<IndexBuilder>();

        return new McqRunner(services.BuildServiceProvider(), config);
    }

    public async Task<McqRun> RunAsync(McqCorpusFile corpus, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var outcomes = new List<McqItemOutcome>();
        foreach (var condition in _config.Conditions)
        {
            Console.Error.WriteLine($"  condition: {condition.Label()} ({corpus.Items.Count} × {_config.Reps} rep(s)) …");
            foreach (var item in corpus.Items)
            {
                ct.ThrowIfCancellationRequested();
                outcomes.Add(await ScoreItemAsync(item, condition, _config.Tuning, ct));
            }
        }

        var summaries = _config.Conditions
            .Select(c => Summarize(c, outcomes.Where(o => o.Condition == c)))
            .ToList();

        var sweeps = new List<McqSweepAxis>();
        if (!string.IsNullOrWhiteSpace(_config.SweepKnob))
        {
            Console.Error.WriteLine();
            sweeps.Add(await SweepAsync(corpus, _config.SweepKnob!, ct));
        }

        return new McqRun(
            McqRun.CurrentSchema, corpus.Version, _config.Model, _config.Temperature, _config.Seed,
            _config.Reps, _config.Tuning, startedAt, DateTimeOffset.UtcNow, summaries, outcomes, sweeps);
    }

    private async Task<McqItemOutcome> ScoreItemAsync(
        McqItem item, McqCondition condition, RagTuning tuning, CancellationToken ct)
    {
        string? context;
        IReadOnlyList<SearchHit> rawHits = [];
        switch (condition)
        {
            case McqCondition.Oracle:
                context = item.Gold;
                break;
            case McqCondition.WithRag:
                (context, rawHits) = await RetrieveAsync(item.Question, tuning, ct);
                break;
            default:  // ClosedBook
                context = null;
                break;
        }

        int correct = 0, parsed = 0;
        char? lastChosen = null;
        string? lastReply = null;

        for (var rep = 0; rep < _config.Reps; rep++)
        {
            ct.ThrowIfCancellationRequested();
            var messages = BuildMessages(item, context);
            var response = await _llm.ChatAsync(messages, tools: null, think: false, ct);

            lastReply = response.IsSuccess ? response.Value!.Content : $"[model error: {response.Error}]";
            if (AnswerParser.TryParse(lastReply, item.Choices.Count, out var letter))
            {
                parsed++;
                lastChosen = letter;
                if (letter == item.AnswerLetter)
                    correct++;
            }
            else
            {
                lastChosen = null;
            }
        }

        var diagnostic = condition == McqCondition.WithRag
            ? BuildDiagnostic(item, context ?? string.Empty, rawHits)
            : null;

        return new McqItemOutcome(
            item.Id, item.Topic, condition, item.AnswerLetter, correct, parsed, _config.Reps,
            lastChosen, lastReply, diagnostic);
    }

    /// <summary>The with-RAG grounding text — produced by the SAME aggregator production ships, over an
    /// in-memory index built with this run's chunk knobs and queried with its TopK/MinScore. Returns the
    /// grounding alongside the raw cosine top-k (pre-MinScore) so the diagnosis can measure recall@k and
    /// see which chunks survived the <c>MaxContextChars</c> cap.</summary>
    private async Task<(string Grounding, IReadOnlyList<SearchHit> RawHits)> RetrieveAsync(
        string query, RagTuning tuning, CancellationToken ct)
    {
        var index = await GetIndexAsync(tuning.ChunkSize, tuning.ChunkOverlap, ct);
        var retrieval = new EvalRetrieval(_embeddings, index, tuning.TopK, tuning.MinScore);
        var options = Options.Create(new SearchOptions
        {
            LocalMinScore = tuning.LocalMinScore,
            MaxContextChars = tuning.MaxContextChars,
            LocalEnabled = true,
            WebEnabled = false,
        });
        var aggregator = new SearchAggregator(retrieval, new NoWebSearch(), options, _aggregatorLogger);
        // The MCQ read only wants the model-facing grounding text (the Summary half of the envelope).
        var grounding = (await aggregator.SearchAsync(query, ct)).Summary;
        return (grounding, retrieval.LastRawHits);
    }

    /// <summary>
    /// Turns the raw top-k + the grounding string into a per-item retrieval diagnostic: for each hit, how
    /// much of the gold passage it lexically covers, whether its text survived into the grounding (the
    /// <c>MaxContextChars</c> cap), and whether it's from the gold's own source doc. This is a measurement
    /// of retrieval only — the answer scoring above is untouched.
    /// </summary>
    private static RetrievalDiagnostic BuildDiagnostic(
        McqItem item, string grounding, IReadOnlyList<SearchHit> rawHits)
    {
        var goldFile = Path.GetFileName(item.Source);
        var hits = rawHits.Select(h => new RetrievedHit(
            SourcePath: h.Chunk.SourcePath,
            HeaderPath: h.Chunk.HeaderPath,
            Score: h.Score,
            GoldOverlap: TextOverlap.Coverage(item.Gold, h.Chunk.Text),
            Survived: !string.IsNullOrEmpty(h.Chunk.Text) && grounding.Contains(h.Chunk.Text, StringComparison.Ordinal),
            RightDoc: string.Equals(Path.GetFileName(h.Chunk.SourcePath), goldFile, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new RetrievalDiagnostic(hits);
    }

    private async Task<RagIndex> GetIndexAsync(int chunkSize, int chunkOverlap, CancellationToken ct)
    {
        var key = (chunkSize, chunkOverlap);
        if (_indexCache.TryGetValue(key, out var cached))
            return cached;

        Console.Error.WriteLine($"    building index (chunk {chunkSize}/{chunkOverlap}) from {_config.CorpusDir} …");
        var outputPath = Path.Combine(_tempDir, $"mcq-{chunkSize}-{chunkOverlap}.krag");
        var options = new IndexBuilderOptions
        {
            Sources = new[] { _config.CorpusDir },
            SearchPattern = "*.md",
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap,
        };

        var report = await _builder.BuildAsync(options, outputPath, previous: null, ct);
        if (report.IsFailure)
            throw new McqRunException($"index build failed: {report.Error}");

        Console.Error.WriteLine(
            $"    index: {report.Value!.TotalChunks} chunk(s) from {report.Value.SourceFiles} file(s)");
        var index = RagIndexFile.ReadFromFile(outputPath);
        _indexCache[key] = index;
        return index;
    }

    private async Task<McqSweepAxis> SweepAsync(McqCorpusFile corpus, string knob, CancellationToken ct)
    {
        var (canonical, values, apply) = SweepGrid.Resolve(knob, _config.Tuning);
        Console.Error.WriteLine($"  sweep: {canonical} ∈ {{{string.Join(", ", values)}}} (with-rag) …");

        var points = new List<McqSweepPoint>();
        foreach (var value in values)
        {
            var tuning = apply(value);
            int correct = 0, parsed = 0, total = 0;
            foreach (var item in corpus.Items)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await ScoreItemAsync(item, McqCondition.WithRag, tuning, ct);
                correct += outcome.Correct;
                parsed += outcome.Parsed;
                total += outcome.Reps;
            }
            points.Add(new McqSweepPoint(value, correct, parsed, total));
            Console.Error.WriteLine($"    {canonical}={value,-8} → {(total == 0 ? 0 : (double)correct / total):P1}");
        }

        return new McqSweepAxis(canonical, points);
    }

    private static McqConditionSummary Summarize(McqCondition condition, IEnumerable<McqItemOutcome> outcomes)
    {
        int correct = 0, parsed = 0, total = 0;
        foreach (var o in outcomes)
        {
            correct += o.Correct;
            parsed += o.Parsed;
            total += o.Reps;
        }
        return new McqConditionSummary(condition, correct, parsed, total);
    }

    private static IReadOnlyList<LlmMessage> BuildMessages(McqItem item, string? context)
    {
        var user = context is null
            ? $"Question: {item.Question}\n\nChoices:\n{item.FormatChoices()}"
            : $"Use the reference material below to answer.\n\nReference material:\n{context}\n\n" +
              $"Question: {item.Question}\n\nChoices:\n{item.FormatChoices()}";

        return new[] { LlmMessage.System(SystemPrompt), LlmMessage.User(user) };
    }

    /// <summary>Builds the layered config the runner reads: eval defaults (appsettings) → the CLI's user
    /// config (so it inherits a configured Ollama endpoint) → env → MCQ overrides. The chat and embed
    /// clients are pinned to the SAME resolved endpoint so they hit one Ollama.</summary>
    private static IConfiguration BuildConfiguration(McqRunConfig config)
    {
        IConfigurationBuilder Layers() => new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(CliUserConfigPath(), optional: true)
            .AddEnvironmentVariables();

        // First pass: resolve the effective endpoint so chat + embed share it.
        var preliminary = Layers().Build();
        var endpoint = config.Endpoint
            ?? preliminary["Ollama:Endpoint"]
            ?? "http://localhost:11434";

        var overrides = new Dictionary<string, string?>
        {
            ["Recording:Enabled"] = "false",
            ["Ollama:Model"] = config.Model,
            ["Ollama:Endpoint"] = endpoint,
            // A generous ceiling so an occasional long generation doesn't truncate a question into an
            // unparsed reply (which the diagnosis would otherwise have to set aside as inconclusive).
            ["Ollama:TimeoutSeconds"] = "900",
            ["Ollama:Temperature"] = config.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Rag:Endpoint"] = endpoint,
        };
        if (config.Seed is int seed) overrides["Ollama:Seed"] = seed.ToString();
        if (!string.IsNullOrWhiteSpace(config.EmbeddingModel)) overrides["Rag:EmbeddingModel"] = config.EmbeddingModel;

        return Layers().AddInMemoryCollection(overrides).Build();
    }

    private static string CliUserConfigPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(home, "kgsm-assistant", "appsettings.json");
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean up the MCQ temp dir {Dir}.", _tempDir);
        }
    }
}

/// <summary>Raised when an MCQ run cannot proceed (e.g. the embedder is unreachable so no index builds).</summary>
internal sealed class McqRunException : Exception
{
    public McqRunException(string message) : base(message) { }
}
