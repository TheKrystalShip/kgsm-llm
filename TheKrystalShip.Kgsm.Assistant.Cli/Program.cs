using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

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

if (!CliOptions.TryParse(args, out var cli, out var parseError))
{
    Console.Error.WriteLine($"kgsm-assistant: {parseError}");
    Console.Error.WriteLine("Try 'kgsm-assistant --help'.");
    return ExitUsage;
}

if (cli.Help)
{
    Console.Out.Write(CliOptions.Usage);
    return ExitOk;
}

// --- Config resolution (§3.2): a single layered surface, low → high precedence:
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
    builder.Configuration["Ollama:Model"] = cli.Model;

// --- Logging (§3.1): quiet by DEFAULT (floor at Warning), everything to stderr so stdout is the reply.
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

// --- Backend wiring: the entire host is three calls (§3.1). -----------------------------------
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
        interactiveStdin: !Console.IsInputRedirected,   // gates interactive confirmation (L8)
        showStatus: !Console.IsOutputRedirected,        // ⚙/✓ progress only when stdout is a TTY
        color: colorErr,
        stderrTty: stderrTty);                          // spinner animates on stderr → gate on its TTY

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

    // No prompt + interactive stdin → the REPL (which also offers /compact).
    var compactor = host.Services.GetRequiredService<IConversationCompactor>();
    return await Repl.RunAsync(runner, interruptor, compactor, canPerformActions, colorErr);
}

// --- helpers ---------------------------------------------------------------------------------

static string DefaultConfigPath()
{
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var configHome = !string.IsNullOrWhiteSpace(xdg)
        ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    return Path.Combine(configHome, "kgsm-assistant", "appsettings.json");
}
