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
        if (tokens.Contains(c.Id.ToUpperInvariant())) return true;
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
