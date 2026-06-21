using TheKrystalShip.Kgsm.Assistant.Eval;

const int ExitOk = 0, ExitRuntime = 1, ExitUsage = 2, ExitCancelled = 130;

if (!EvalOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine($"kgsm-assistant-eval: {error}");
    Console.Error.WriteLine("Try 'kgsm-assistant-eval --help'.");
    return ExitUsage;
}

if (options.Help)
{
    Console.Out.WriteLine(EvalOptions.Usage);
    return ExitOk;
}

if (options.IsCompare)
    return Compare.Run(options.CompareBase!, options.CompareHead!, Console.Out);

// --- mcq mode (ground-truth answer accuracy) ---------------------------------------------------
// Branches BEFORE the kgsm check: the MCQ mode calls the model directly and never touches a kgsm
// host (only Ollama for chat + embeddings), so requiring kgsm here would be wrong.
if (options.IsMcq)
    return await RunMcqAsync(options);

// --- run mode ----------------------------------------------------------------------------------

var kgsmPath = Harness.ResolveKgsmPath(options);
if (string.IsNullOrWhiteSpace(kgsmPath) || !File.Exists(kgsmPath))
{
    Console.Error.WriteLine(
        $"kgsm-assistant-eval: kgsm not found{(string.IsNullOrWhiteSpace(kgsmPath) ? "" : $" at '{kgsmPath}'")}. " +
        "Set it with --kgsm <path>, KGSM__Path, or the CLI's appsettings.json.");
    return ExitUsage;
}

var cases = Filter(BenchmarkSuite.Cases, options.Filter, out var filterError);
if (filterError is not null)
{
    Console.Error.WriteLine($"kgsm-assistant-eval: {filterError}");
    return ExitUsage;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Harness harness;
try
{
    harness = Harness.Build(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"kgsm-assistant-eval: startup failed — {ex.Message}");
    return ExitRuntime;
}

try
{
    Console.Error.WriteLine($"Resolving fixtures against the live host (kgsm: {kgsmPath}) …");
    var fixtures = await harness.PreflightAsync(cts.Token);
    if (fixtures is null)
        return ExitRuntime;

    Console.Error.WriteLine();
    Console.Error.WriteLine($"Running {cases.Count} case(s) × {options.Reps} rep(s) against {options.Model} …");
    var run = await harness.RunAsync(fixtures, cases, cts.Token);

    Scorecard.Render(run, Console.Out);
    if (options.Transcript)
        Transcripts.Render(run, Console.Out);

    var outPath = options.OutPath ?? DefaultOutPath(options.Model);
    run.Save(outPath);
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Result written to {outPath}");
    Console.Error.WriteLine($"Compare later with:  kgsm-assistant-eval compare {outPath} <newer-run.json>");
    return ExitOk;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("cancelled.");
    return ExitCancelled;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"kgsm-assistant-eval: run failed — {ex.Message}");
    return ExitRuntime;
}

// --- helpers -----------------------------------------------------------------------------------

static IReadOnlyList<BenchmarkCase> Filter(IReadOnlyList<BenchmarkCase> all, IReadOnlyList<string> filter, out string? error)
{
    error = null;
    if (filter.Count == 0) return all;

    var tokens = filter.Select(f => f.ToUpperInvariant()).ToHashSet();
    bool Matches(BenchmarkCase c)
    {
        var id = c.Id.ToUpperInvariant();
        if (tokens.Contains(id)) return true;
        // Id-prefix, so "--filter G" runs the whole G group (G1..G5), "C" the C group, etc.
        if (tokens.Any(t => id.StartsWith(t))) return true;
        var dims = c.Steps.SelectMany(s => s.Checks).Select(ch => ch.Dimension.ToString().Split('_')[0]).ToHashSet();
        return tokens.Overlaps(dims);
    }

    var matched = all.Where(Matches).ToList();
    if (matched.Count == 0)
        error = $"--filter '{string.Join(",", filter)}' matched no cases. Use case ids (A1,C8) or dimension letters (A–E).";
    return matched;
}

static string DefaultOutPath(string model)
{
    var safeModel = model.Replace(':', '_').Replace('/', '_');
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return Path.Combine("eval-results", $"{safeModel}-{stamp}.json");
}

static async Task<int> RunMcqAsync(EvalOptions options)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        var mcqFile = options.McqFile ?? McqCorpus.DefaultPath;
        var corpus = McqCorpus.Load(mcqFile);

        var corpusDir = options.CorpusDir ?? McqCorpus.DefaultCorpusDir;
        var needsIndex = options.Conditions.Contains(McqCondition.WithRag) || options.SweepKnob is not null;
        if (needsIndex && !Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine(
                $"kgsm-assistant-eval: with-rag needs a docs dir to index, but '{corpusDir}' was not found. " +
                "Pass --corpus <dir>, or run --conditions closed-book,oracle.");
            return 2; // ExitUsage
        }

        var config = new McqRunConfig(
            options.Model, options.Endpoint, options.McqTemperature, options.Seed, options.McqReps,
            options.EmbeddingModel, corpusDir, mcqFile, options.Conditions, options.McqTuning,
            options.SweepKnob, options.Verbose);

        Console.Error.WriteLine(
            $"MCQ eval · {corpus.Items.Count} question(s) (corpus {corpus.Version}) · model {config.Model} · " +
            $"conditions {string.Join(",", config.Conditions.Select(c => c.Label()))} …");

        using var runner = McqRunner.Build(config);
        var run = await runner.RunAsync(corpus, cts.Token);

        McqScorecard.Render(run, Console.Out);
        if (options.Transcript)
            McqScorecard.RenderTranscript(run, Console.Out);

        var outPath = options.OutPath ?? DefaultMcqOutPath(options.Model);
        run.Save(outPath);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Result written to {outPath}");
        return 0; // ExitOk
    }
    catch (McqCorpusException ex)
    {
        Console.Error.WriteLine($"kgsm-assistant-eval: {ex.Message}");
        return 2; // ExitUsage
    }
    catch (McqRunException ex)
    {
        Console.Error.WriteLine($"kgsm-assistant-eval: {ex.Message}");
        return 1; // ExitRuntime
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("cancelled.");
        return 130; // ExitCancelled
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"kgsm-assistant-eval: MCQ run failed — {ex.Message}");
        return 1; // ExitRuntime
    }
}

static string DefaultMcqOutPath(string model)
{
    var safeModel = model.Replace(':', '_').Replace('/', '_');
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return Path.Combine("eval-results", $"mcq-{safeModel}-{stamp}.json");
}
