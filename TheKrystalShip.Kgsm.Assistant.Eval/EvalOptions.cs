using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>Parsed command line for the eval runner. Two modes: a benchmark run, or <c>compare</c>.</summary>
internal sealed class EvalOptions
{
    public bool Help { get; private set; }
    public bool Verbose { get; private set; }
    public bool Transcript { get; private set; }
    public bool Diagnose { get; private set; }

    // compare mode
    public bool IsCompare { get; private set; }
    public string? CompareBase { get; private set; }
    public string? CompareHead { get; private set; }

    // run mode
    public string Model { get; private set; } = "gemma4:12b";
    public double Temperature { get; private set; } = 0.3;
    public int? Seed { get; private set; }
    public int Reps { get; private set; } = 3;
    public string? Endpoint { get; private set; }
    public string? KgsmPath { get; private set; }
    public string? PromptsDir { get; private set; }
    public string? OutPath { get; private set; }
    public IReadOnlyList<string> Filter { get; private set; } = Array.Empty<string>();

    // voice mode (spoken-reply length, measured against the same prompts answered for a screen)
    public bool IsVoice { get; private set; }

    // mcq mode (ground-truth answer accuracy + retrieval tuning — Phase 5)
    public bool IsMcq { get; private set; }
    public string? CorpusDir { get; private set; }
    public string? McqFile { get; private set; }
    public string? EmbeddingModel { get; private set; }
    public string? SweepKnob { get; private set; }
    public IReadOnlyList<McqCondition> Conditions { get; private set; } =
        new[] { McqCondition.ClosedBook, McqCondition.WithRag, McqCondition.Oracle };

    // mcq retrieval/chunking knobs — initialised to the shipped defaults, overridable per run/sweep.
    private int _chunkSize = RagTuning.Default.ChunkSize;
    private int _chunkOverlap = RagTuning.Default.ChunkOverlap;
    private int _topK = RagTuning.Default.TopK;
    private double _minScore = RagTuning.Default.MinScore;
    private double _localMinScore = RagTuning.Default.LocalMinScore;
    private int _maxContext = RagTuning.Default.MaxContextChars;
    public RagTuning McqTuning => new(_chunkSize, _chunkOverlap, _topK, _minScore, _localMinScore, _maxContext);

    // MCQ wants greedy determinism + a single rep (a stable accuracy number), NOT the routing harness's
    // temp 0.3 / 3 reps — so default temp 0 and reps 1 here UNLESS the user set them explicitly.
    private bool _tempSet, _repsSet;
    public double McqTemperature => _tempSet ? Temperature : 0.0;
    public int McqReps => _repsSet ? Reps : 1;

    public const string Usage =
        """
        kgsm-assistant-eval — reproducible behavioral benchmark for the kgsm assistant.

        USAGE
          kgsm-assistant-eval [options]                 run the ROUTING benchmark (did it call the right tool?)
          kgsm-assistant-eval mcq [options]             run the GROUND-TRUTH MCQ benchmark (is the answer correct?)
          kgsm-assistant-eval voice [options]           measure SPOKEN reply length (written vs voice style)
          kgsm-assistant-eval compare <base> <head>     diff two routing result files (regressions / improvements)

        RUN OPTIONS
          --model <tag>        Ollama model to benchmark (default: gemma4:12b; e.g. qwen3.5:9b)
          -n, --reps <int>     repetitions per prompt — pass-rate over N absorbs temp nondeterminism (default: 3)
          --seed <int>         fix the sampling seed for a reproducible single-path run (default: unseeded)
          --temp <float>       sampling temperature (default: 0.3, the shipped value)
          --transcript         after the scorecard, print every case's full conversation for live judging
          --filter <csv>       only run matching cases — by id (A1,C8) or dimension letter (D,E)
          --prompts <dir>      prompt-override dir to test (default: the CLI's tuned prompts dir)
          --shipped-prompts    test the shipped DEFAULT prompts (ignore any override files)
          --endpoint <url>     Ollama endpoint (default: from config / http://localhost:11434)
          --kgsm <path>        path to kgsm.sh (default: from CLI config / KGSM__Path)
          --out <path>         result JSON path (default: ./eval-results/<model>-<timestamp>.json)
          -v, --verbose        backend debug logging on stderr
          -h, --help           this help

        MCQ OPTIONS (mode: mcq) — needs Ollama (chat + embedder), NOT a kgsm host
          --conditions <csv>   which conditions to run (default: closed-book,with-rag,oracle)
          --corpus <dir>       docs dir to index for with-rag (default: the shipped real-docs snapshot)
          --mcq-file <path>    the question corpus JSON (default: the shipped mcq/questions.json)
          --embed-model <tag>  embedding model for retrieval (default: from config / embeddinggemma)
          --chunk-size <int>   chunk target chars at index time (default: 2000)
          --chunk-overlap <int> chunk overlap chars (default: 200)
          --topk <int>         chunks retrieved per query (default: 5)
          --min-score <float>  retrieval cosine floor (default: 0.0)
          --local-min-score <float>  "answer from docs" threshold (default: 0.35)
          --max-context <int>  cap on injected grounding chars (default: 6000)
          --sweep <knob>       sweep one knob over a grid (with-rag): chunk-size | chunk-overlap |
                               top-k | min-score | local-min-score | max-context
          --diagnose           after the lift chart, report retrieval recall@k and bucket the
                               with-rag→oracle gap (gold missed / dropped from context / model ceiling)
          (mcq defaults to temp 0 / 1 rep for a stable accuracy number — override with --temp / --reps)

        VOICE OPTIONS (mode: voice) — needs Ollama + a kgsm host, like the routing run
          (takes --model / --temp / --seed / --prompts / --shipped-prompts / --transcript / --kgsm)
          Runs each spoken-style question twice — once for a screen, once for a speaker — and reports
          reply length in characters alongside the trajectory floor (tool called, confirmation staged
          AND stated). It only ever stages, never confirms.

        The routing harness drives the real assistant in-process and only ever STAGES actions (never
        confirms), so a full run is non-destructive; it prints a loud inventory preflight and aborts on
        empty. The mcq mode calls the model directly (no tools, no kgsm, no path to any action) and
        scores answer correctness — closed-book vs with-rag vs oracle — to reproduce the retrieval lift.
        """;

    public static bool TryParse(string[] args, out EvalOptions options, out string? error)
    {
        options = new EvalOptions();
        error = null;
        var seenPositional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help": options.Help = true; return true;
                case "-v" or "--verbose": options.Verbose = true; break;
                case "--transcript": options.Transcript = true; break;
                case "--diagnose": options.Diagnose = true; break;
                case "compare": options.IsCompare = true; break;
                case "mcq": options.IsMcq = true; break;
                case "voice": options.IsVoice = true; break;
                case "--shipped-prompts": options.PromptsDir = ShippedPromptsDir(); break;

                case "--corpus": if (!Next(args, ref i, out var cd, out error)) return false; options.CorpusDir = cd; break;
                case "--mcq-file": if (!Next(args, ref i, out var mf, out error)) return false; options.McqFile = mf; break;
                case "--embed-model": if (!Next(args, ref i, out var em, out error)) return false; options.EmbeddingModel = em; break;
                case "--sweep": if (!Next(args, ref i, out var sw, out error)) return false; options.SweepKnob = sw; break;
                case "--conditions":
                    if (!Next(args, ref i, out var conds, out error)) return false;
                    if (!TryParseConditions(conds, out var parsed, out error)) return false;
                    options.Conditions = parsed; break;

                case "--chunk-size": if (!NextInt(args, ref i, out options._chunkSize, out error)) return false; break;
                case "--chunk-overlap": if (!NextInt(args, ref i, out options._chunkOverlap, out error)) return false; break;
                case "--topk" or "--top-k": if (!NextInt(args, ref i, out options._topK, out error)) return false; break;
                case "--max-context": if (!NextInt(args, ref i, out options._maxContext, out error)) return false; break;
                case "--min-score": if (!NextDouble(args, ref i, out options._minScore, out error)) return false; break;
                case "--local-min-score": if (!NextDouble(args, ref i, out options._localMinScore, out error)) return false; break;

                case "--model": if (!Next(args, ref i, out var m, out error)) return false; options.Model = m; break;
                case "--endpoint": if (!Next(args, ref i, out var ep, out error)) return false; options.Endpoint = ep; break;
                case "--kgsm": if (!Next(args, ref i, out var kp, out error)) return false; options.KgsmPath = kp; break;
                case "--prompts": if (!Next(args, ref i, out var pd, out error)) return false; options.PromptsDir = pd; break;
                case "--out": if (!Next(args, ref i, out var op, out error)) return false; options.OutPath = op; break;
                case "--filter":
                    if (!Next(args, ref i, out var f, out error)) return false;
                    options.Filter = f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;

                case "-n" or "--reps":
                    if (!Next(args, ref i, out var r, out error)) return false;
                    if (!int.TryParse(r, out var reps) || reps < 1) { error = $"--reps expects a positive integer, got '{r}'."; return false; }
                    options.Reps = reps; options._repsSet = true; break;

                case "--seed":
                    if (!Next(args, ref i, out var s, out error)) return false;
                    if (!int.TryParse(s, out var seed)) { error = $"--seed expects an integer, got '{s}'."; return false; }
                    options.Seed = seed; break;

                case "--temp":
                    if (!Next(args, ref i, out var t, out error)) return false;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp)) { error = $"--temp expects a number, got '{t}'."; return false; }
                    options.Temperature = temp; options._tempSet = true; break;

                default:
                    if (a.StartsWith('-')) { error = $"unknown option '{a}'."; return false; }
                    seenPositional.Add(a);
                    break;
            }
        }

        if (options.IsCompare && options.IsMcq)
        {
            error = "choose one mode: 'mcq' or 'compare', not both.";
            return false;
        }

        if (options.IsCompare)
        {
            if (seenPositional.Count != 2) { error = "compare needs exactly two result files: compare <base> <head>."; return false; }
            options.CompareBase = seenPositional[0];
            options.CompareHead = seenPositional[1];
        }
        else if (seenPositional.Count > 0)
        {
            error = $"unexpected argument '{seenPositional[0]}'.";
            return false;
        }

        return true;
    }

    private static bool NextInt(string[] args, ref int i, out int value, out string? error)
    {
        value = 0;
        if (!Next(args, ref i, out var raw, out error)) return false;
        if (!int.TryParse(raw, out value) || value < 0) { error = $"option '{args[i - 1]}' expects a non-negative integer, got '{raw}'."; return false; }
        return true;
    }

    private static bool NextDouble(string[] args, ref int i, out double value, out string? error)
    {
        value = 0;
        if (!Next(args, ref i, out var raw, out error)) return false;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { error = $"option '{args[i - 1]}' expects a number, got '{raw}'."; return false; }
        return true;
    }

    private static bool TryParseConditions(string csv, out IReadOnlyList<McqCondition> conditions, out string? error)
    {
        error = null;
        var list = new List<McqCondition>();
        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!McqConditions.TryParse(token, out var c))
            {
                conditions = Array.Empty<McqCondition>();
                error = $"--conditions: unknown condition '{token}'. Use closed-book, with-rag, oracle.";
                return false;
            }
            if (!list.Contains(c)) list.Add(c);
        }
        if (list.Count == 0)
        {
            conditions = Array.Empty<McqCondition>();
            error = "--conditions needs at least one of: closed-book, with-rag, oracle.";
            return false;
        }
        conditions = list;
        return true;
    }

    private static bool Next(string[] args, ref int i, out string value, out string? error)
    {
        if (i + 1 >= args.Length) { value = ""; error = $"option '{args[i]}' expects a value."; return false; }
        value = args[++i];
        error = null;
        return true;
    }

    // The text that SHIPS: deploy/prompts/ in the repo when running from a build tree, else the set
    // installed beside the binary. Measuring the shipped prompt means reading the shipped files —
    // there is no compiled-in copy to fall back to, and an empty directory now means "no prompt at
    // all" rather than "the defaults".
    private static string ShippedPromptsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "prompts");
            if (File.Exists(Path.Combine(candidate, "tools.json")))
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "prompts"));
    }
}
