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

// --- Config resolution (§3.2): defaults → optional file → env → CLI override (low → high) ----
var configPath = cli.ConfigPath
    ?? Environment.GetEnvironmentVariable("KGSM_ASSISTANT_CONFIG")
    ?? DefaultConfigPath();

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Configuration.AddInMemoryCollection(BuiltInDefaults());
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
if (!string.IsNullOrWhiteSpace(cli.Model))
    builder.Configuration["Ollama:Model"] = cli.Model;   // an explicit flag beats config + env

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
        $"{configPath}) to your kgsm.sh.");
    return ExitUsage;
}
if (!File.Exists(kgsmPath))
{
    Console.Error.WriteLine(
        $"kgsm-assistant: kgsm not found at '{kgsmPath}'. Set KGSM__Path (or KGSM:Path in " +
        $"{configPath}) to your kgsm.sh.");
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

    var runner = new CliRunner(
        assistant, inventory, canPerformActions,
        interactiveStdin: !Console.IsInputRedirected,   // gates interactive confirmation (L8)
        showStatus: !Console.IsOutputRedirected,        // ⚙/✓ progress only when stdout is a TTY
        color: !Console.IsErrorRedirected && !noColor);

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

    // No prompt + interactive stdin → the REPL.
    return await Repl.RunAsync(runner, interruptor, canPerformActions);
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

// The lowest config layer. The Llm/inventory option classes already carry sensible defaults, so
// the only built-in default worth seeding is the established host kgsm path (overridable by
// file/env/flag); validation above still catches a wrong path.
static Dictionary<string, string?> BuiltInDefaults() => new()
{
    ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
};
