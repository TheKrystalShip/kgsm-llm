using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Cli;
using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;

// Exit codes: 0 ok · 1 runtime failure · 2 usage/config error · 130 cancelled (SIGINT).
const int ExitOk = 0, ExitRuntime = 1, ExitUsage = 2, ExitCancelled = 130;

// Subcommand dispatch: `kgsm-assistant index ...` builds the RAG index via the shared core engine,
// reusing the layered config below but NONE of the chat/KGSM backend. Detected before flag parsing
// because its sub-flags (--full, --source) aren't chat options; only --config is pulled out here so
// config resolution can honor it — IndexCommand parses the rest after the config is built.
var indexMode = args.Length > 0 && args[0] == "index";
var indexArgs = indexMode ? args[1..] : [];

CliOptions cli;
if (indexMode)
{
    cli = new CliOptions { ConfigPath = ConfigPathFrom(indexArgs) };
}
else if (!CliOptions.TryParse(args, out cli, out var parseError))
{
    Console.Error.WriteLine($"kgsm-assistant: {parseError}");
    Console.Error.WriteLine("Try 'kgsm-assistant --help'.");
    return ExitUsage;
}

if (!indexMode && cli.Help)
{
    Console.Out.Write(CliOptions.Usage);
    return ExitOk;
}

// --- Config resolution: a single layered surface, low → high precedence:
//     embedded defaults → sidecar appsettings.json (next to the binary) → operator's file →
//     environment → --model flag. No defaults are hardcoded in C#; every knob lives in the one
//     canonical appsettings.json (embedded for self-sufficiency, copied beside the binary to edit).
var userConfigPath = cli.ConfigPath
    ?? Environment.GetEnvironmentVariable("KGSM_ASSISTANT_CONFIG")
    ?? DefaultConfigPath();

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

// 1. Baked-in defaults: the appsettings.json embedded at build time. Guarantees the lone binary
//    carries its full default surface even when deployed with no sidecar files at all.
using (var embeddedDefaults = Assembly.GetExecutingAssembly()
    .GetManifestResourceStream("kgsm-assistant.appsettings.json"))
{
    if (embeddedDefaults is not null)
        builder.Configuration.AddJsonStream(embeddedDefaults);
}
// 2. The editable template shipped next to the binary (overrides the embedded defaults).
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
// 3. The operator's own config file ($KGSM_ASSISTANT_CONFIG / --config / XDG location).
builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);
// 4. Environment (Section__Key) — the channel for secrets, e.g. WebSearch__ApiKey.
builder.Configuration.AddEnvironmentVariables();
// 5. An explicit --model flag beats config + env.
if (!string.IsNullOrWhiteSpace(cli.Model))
    builder.Configuration["Llm:Model"] = cli.Model;

// The index verb needs only the resolved config — not the chat/KGSM backend. Dispatch here, before
// any recording/prompt/KGSM setup or backend wiring, so a host with Ollama but no kgsm can index.
if (indexMode)
    return await IndexCommand.RunAsync(builder.Configuration, indexArgs);

// Recording (the self-improvement transcript corpus): the generic lib holds no host path knowledge,
// so the CLI supplies the XDG data default when recording is on but no directory was set (same shape
// as config resolution above). Daily yyyy-MM-dd.jsonl files land under this dir.
if (builder.Configuration.GetValue("Recording:Enabled", false)
    && string.IsNullOrWhiteSpace(builder.Configuration["Recording:Directory"]))
{
    builder.Configuration["Recording:Directory"] = DefaultRecordingDir();
}

// Prompt-tuning artifacts (editable prompt segments + tool descriptions) default to an XDG config
// location, re-read each turn so an edit applies to the next turn with no restart.
if (string.IsNullOrWhiteSpace(builder.Configuration["Prompts:Directory"]))
    builder.Configuration["Prompts:Directory"] = DefaultPromptsDir();

// --label tags this run's recorded turns, so a prompt/tool-description edit can be A/B'd against the
// transcript corpus (filter by it later). Maps onto the recorder's Recording:Label knob.
if (!string.IsNullOrWhiteSpace(cli.Label))
    builder.Configuration["Recording:Label"] = cli.Label;

// --- Logging: quiet by DEFAULT (floor at Warning), everything to stderr so stdout is the reply.
var noColor = cli.NoColor || Environment.GetEnvironmentVariable("NO_COLOR") is not null;
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(cli.Verbose ? LogLevel.Debug : LogLevel.Warning);
// Simple formatter; honor --no-color / NO_COLOR for framework log lines too (not just our renderer).
builder.Logging.AddSimpleConsole(o =>
    o.ColorBehavior = noColor ? LoggerColorBehavior.Disabled : LoggerColorBehavior.Default);
// Route EVERY level to stderr (threshold at Trace) so stdout carries only the reply.
// LogToStandardErrorThreshold lives on the logger options, not the formatter options.
builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// --- Friendly startup validation: a bad KGSM path is a one-liner, never a stack trace. -------
var kgsmPath = builder.Configuration["KGSM:Path"];
if (string.IsNullOrWhiteSpace(kgsmPath))
{
    Console.Error.WriteLine(
        "kgsm-assistant: KGSM path not configured. Set KGSM__Path (or KGSM:Path in " +
        $"{userConfigPath}) to your kgsm.sh.");
    return ExitUsage;
}
if (!File.Exists(kgsmPath))
{
    Console.Error.WriteLine(
        $"kgsm-assistant: kgsm not found at '{kgsmPath}'. Set KGSM__Path (or KGSM:Path in " +
        $"{userConfigPath}) to your kgsm.sh.");
    return ExitUsage;
}

// --- Backend wiring: the entire host is three calls. ------------------------------------------
builder.Services.AddLocalLlm(builder.Configuration);     // Ollama client, conversation store, agent loop
builder.Services.AddKgsmAssistant();                     // prompt builder, dispatcher, policy, IServerAssistant
builder.Services.AddKgsmAdapters(builder.Configuration); // kgsm-lib graph + ports + Tavily (socket-safe)

IHost host;
try
{
    host = builder.Build();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"kgsm-assistant: startup failed — {ex.Message}");
    return ExitRuntime;
}

using (host)
{
    // The prompts and tool definitions are files. Prove they are there and coherent before asking a
    // question, so a missing or mis-edited one is a sentence on stderr rather than a stack trace —
    // or, worse for a prompt segment, an assistant quietly behaving like something else.
    try
    {
        AssistantTextCheck.Validate(host.Services);
    }
    catch (AssistantTextUnavailableException ex)
    {
        Console.Error.WriteLine($"kgsm-assistant: {ex.Message}");
        return ExitRuntime;
    }

    // Provenance once per process: every kgsm mutation this session runs is attributed to cli:<osuser>.
    var invocation = host.Services.GetRequiredService<IInvocationContext>();
    using var provenance = invocation.Begin(Invocation.ForCli(Environment.UserName));

    var canPerformActions = !cli.ReadOnly;   // D1: authorized by default, --read-only opts down
    var assistant = host.Services.GetRequiredService<IServerAssistant>();
    var inventory = host.Services.GetRequiredService<IInventoryInvalidation>();

    var stderrTty = !Console.IsErrorRedirected;
    var colorErr = stderrTty && !noColor;
    var runner = new CliRunner(
        assistant, inventory, canPerformActions,
        interactiveStdin: !Console.IsInputRedirected,
        showStatus: !Console.IsOutputRedirected,
        color: colorErr,
        stderrTty: stderrTty,
        // --think/--no-think win; absent either, fall back to the Ollama:Think config default
        // (so the REPL's "/think (default: from config)" is accurate and Llm__Think is honored).
        think: cli.Think ?? builder.Configuration.GetValue("Llm:Think", false));

    // Ctrl-C cancels the running turn (aborts Ollama generation, L3) rather than the process.
    using var interruptor = new TurnInterruptor();

    // --- Mode dispatch (L5): one-shot (positional arg OR piped stdin) vs interactive REPL. ------
    var oneShotPrompt = cli.Prompt ?? (Console.IsInputRedirected ? Console.In.ReadToEnd() : null);

    if (oneShotPrompt is not null)
    {
        if (string.IsNullOrWhiteSpace(oneShotPrompt))
        {
            Console.Error.WriteLine("kgsm-assistant: empty prompt.");
            Console.Error.WriteLine("Try 'kgsm-assistant --help'.");
            return ExitUsage;
        }

        var (completed, ok) = await interruptor.RunAsync(
            ct => runner.RunTurnAsync($"cli:{Guid.NewGuid():N}", oneShotPrompt.Trim(), ct));
        if (!completed)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("cancelled.");
            return ExitCancelled;
        }
        return ok ? ExitOk : ExitRuntime;
    }

    // No prompt + interactive stdin → the REPL, whose commands come from the shared ChatCommands
    // catalog.
    var compactor = host.Services.GetRequiredService<IConversationCompactor>();
    var store = host.Services.GetRequiredService<IConversationStore>();

    // What /tools lists, resolved the same way the turn selects them — the authorized set, minus
    // `search` when nothing backs it. Listing a tool the turn would reject would be the surface lying
    // about its own reach.
    var catalog = host.Services.GetRequiredService<IToolCatalog>();
    var offered = canPerformActions ? catalog.All : catalog.ReadOnly;
    if (!host.Services.GetRequiredService<IOptions<SearchOptions>>().Value.Available)
        offered = [.. offered.Where(t => t.Tool != LlmTools.Search)];

    return await Repl.RunAsync(
        runner, interruptor, compactor, store, offered, canPerformActions, colorErr);
}

// --- helpers ---------------------------------------------------------------------------------

// The `index` verb's --config must be honored during config resolution (before IndexCommand parses
// the rest of its sub-args), so pull just that one value out of the verb's args here.
static string? ConfigPathFrom(string[] verbArgs)
{
    for (var i = 0; i < verbArgs.Length; i++)
    {
        if (verbArgs[i] == "--config" && i + 1 < verbArgs.Length) return verbArgs[i + 1];
        if (verbArgs[i].StartsWith("--config=", StringComparison.Ordinal)) return verbArgs[i]["--config=".Length..];
    }
    return null;
}

static string DefaultConfigPath()
{
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var configHome = !string.IsNullOrWhiteSpace(xdg)
        ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    return Path.Combine(configHome, "kgsm-assistant", "appsettings.json");
}

// Transcript corpus lives under the XDG *data* home (not config — it's generated data, not settings).
static string DefaultRecordingDir()
{
    var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    var dataHome = !string.IsNullOrWhiteSpace(xdg)
        ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    return Path.Combine(dataHome, "kgsm-assistant", "transcripts");
}

// The prompts and tool definitions the assistant RUNS on, installed by deploy.sh beside the binary
// (../prompts, since the CLI lands in <prefix>/cli). A personal copy under the XDG config home still
// wins when one exists, so a developer can try wording out without touching the installed set — but
// the installed set is the default, because the CLI and the service answering the same question
// differently, because one of them found a stale file in a home directory, is not a difference
// anybody would think to look for.
static string DefaultPromptsDir()
{
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var configHome = !string.IsNullOrWhiteSpace(xdg)
        ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    var personal = Path.Combine(configHome, "kgsm-assistant", "prompts");
    if (File.Exists(Path.Combine(personal, DiskToolCatalog.FileName)))
        return personal;

    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "prompts"));
}
