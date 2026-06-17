using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>Parsed command line for the eval runner. Two modes: a benchmark run, or <c>compare</c>.</summary>
internal sealed class EvalOptions
{
    public bool Help { get; private set; }
    public bool Verbose { get; private set; }
    public bool Transcript { get; private set; }

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

    public const string Usage =
        """
        kgsm-assistant-eval — reproducible behavioral benchmark for the kgsm assistant.

        USAGE
          kgsm-assistant-eval [options]                 run the benchmark, print a scorecard, write a result file
          kgsm-assistant-eval compare <base> <head>     diff two result files (regressions / improvements)

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

        The harness drives the real assistant in-process and only ever STAGES actions (never confirms),
        so a full run is non-destructive. It prints a loud inventory preflight and aborts on empty.
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
                case "compare": options.IsCompare = true; break;
                case "--shipped-prompts": options.PromptsDir = ShippedPromptsDir(); break;

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
                    options.Reps = reps; break;

                case "--seed":
                    if (!Next(args, ref i, out var s, out error)) return false;
                    if (!int.TryParse(s, out var seed)) { error = $"--seed expects an integer, got '{s}'."; return false; }
                    options.Seed = seed; break;

                case "--temp":
                    if (!Next(args, ref i, out var t, out error)) return false;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp)) { error = $"--temp expects a number, got '{t}'."; return false; }
                    options.Temperature = temp; break;

                default:
                    if (a.StartsWith('-')) { error = $"unknown option '{a}'."; return false; }
                    seenPositional.Add(a);
                    break;
            }
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

    private static bool Next(string[] args, ref int i, out string value, out string? error)
    {
        if (i + 1 >= args.Length) { value = ""; error = $"option '{args[i]}' expects a value."; return false; }
        value = args[++i];
        error = null;
        return true;
    }

    // An empty, throwaway dir forces SystemPromptBuilder to fall back past file overrides to the
    // shipped constants — so --shipped-prompts measures KgsmAssistantPrompts, not local edits.
    private static string ShippedPromptsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kgsm-eval-shipped-prompts");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
