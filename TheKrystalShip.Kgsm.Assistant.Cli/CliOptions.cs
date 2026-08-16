namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// Parsed command-line surface for the assistant CLI. Flags toggle session policy + rendering;
/// the remaining non-flag tokens are the one-shot prompt (joined with spaces). Absent prompt +
/// an interactive stdin means REPL mode (decided by the caller, not here).
/// </summary>
internal sealed record CliOptions
{
    /// <summary>Demote the session to reads only (D1: authorized by default, this opts down).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Enable thinking/reasoning mode for this session (model generates internal chain-of-thought).</summary>
    public bool? Think { get; init; }

    /// <summary>Raise the log floor to Debug (default is quiet: Warning), all to stderr.</summary>
    public bool Verbose { get; init; }

    /// <summary>Print usage and exit.</summary>
    public bool Help { get; init; }

    /// <summary>Force color off even on a TTY (also honored: the NO_COLOR env var).</summary>
    public bool NoColor { get; init; }

    /// <summary>Override the Ollama model tag for this run (beats config + env).</summary>
    public string? Model { get; init; }

    /// <summary>Override the config-file path (beats $KGSM_ASSISTANT_CONFIG + the default location).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Experiment label stamped on every recorded turn this run (for A/B-ing prompt edits).</summary>
    public string? Label { get; init; }

    /// <summary>The positional one-shot prompt, or null when none was given (→ REPL / stdin).</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Parses <paramref name="args"/>. Returns false with <paramref name="error"/> set on an
    /// unknown flag or a value-flag missing its value; the caller prints the error + usage hint.
    /// </summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        bool readOnly = false, verbose = false, help = false, noColor = false;
        bool? think = null;
        string? model = null, configPath = null, label = null;
        var positional = new List<string>();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--read-only":
                    readOnly = true;
                    break;
                case "--think":
                    think = true;
                    break;
                case "--no-think":
                    think = false;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "--model":
                    if (!TryTakeValue(args, ref i, out model, out error)) { options = Empty; return false; }
                    break;
                case "--config":
                    if (!TryTakeValue(args, ref i, out configPath, out error)) { options = Empty; return false; }
                    break;
                case "--label":
                    if (!TryTakeValue(args, ref i, out label, out error)) { options = Empty; return false; }
                    break;
                default:
                    if (arg.StartsWith("--model=", StringComparison.Ordinal))
                        model = arg["--model=".Length..];
                    else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                        configPath = arg["--config=".Length..];
                    else if (arg.StartsWith("--label=", StringComparison.Ordinal))
                        label = arg["--label=".Length..];
                    else if (arg.Length > 1 && arg[0] == '-' && arg != "-")
                    {
                        // An unknown flag — fail loudly rather than silently swallowing it into the prompt.
                        error = $"unknown option '{arg}'";
                        options = Empty;
                        return false;
                    }
                    else
                    {
                        positional.Add(arg);
                    }
                    break;
            }
        }

        options = new CliOptions
        {
            ReadOnly = readOnly,
            Think = think,
            Verbose = verbose,
            Help = help,
            NoColor = noColor,
            Model = model,
            ConfigPath = configPath,
            Label = label,
            Prompt = positional.Count > 0 ? string.Join(' ', positional) : null,
        };
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"option '{args[i]}' requires a value";
            return false;
        }
        value = args[++i];
        error = null;
        return true;
    }

    private static CliOptions Empty => new();

    public const string Usage =
        """
        kgsm-assistant — ask the KGSM server assistant from the terminal

        USAGE:
          kgsm-assistant [options] "your question"     one-shot: prints the reply and exits
          echo "your question" | kgsm-assistant        one-shot from stdin (piped)
          kgsm-assistant [options]                     interactive REPL (a TTY with no prompt)
          kgsm-assistant index [--full] [--source p]   build the RAG index (see 'index --help')

        OPTIONS:
          --read-only        reads only — never offer or run mutating/destructive actions
          --think            enable thinking/reasoning mode (model generates internal chain-of-thought)
          --no-think         disable thinking/reasoning mode
          --model <tag>      override the Ollama model (e.g. gemma4:12b)
          --config <path>    use this config file instead of the default location
          --label <name>     tag this run's recorded turns (to A/B a prompt/tool edit)
          --no-color         disable colored output (also honored: NO_COLOR)
          --verbose          show debug logs on stderr (default is quiet)
          -h, --help         show this help and exit

        TUNING (edit without recompiling; applies on the next turn):
          the prompt text and tool definitions the assistant runs on are FILES,
          installed by deploy/deploy.sh (preamble.md, actions-allowed.md,
          actions-auto.md, actions-denied.md, voice.md, tools.json). Edit one and
          the next turn uses it; tools.json is re-read on restart. Point
          Prompts:Directory at them.

        CONFIG (low → high precedence):
          embedded defaults  →  appsettings.json beside the binary  →  $KGSM_ASSISTANT_CONFIG
          or ~/.config/kgsm-assistant/appsettings.json  →  environment (Section__Key, e.g.
          KGSM__Path, Llm__Model, WebSearch__ApiKey)  →  --model

        """;
}
