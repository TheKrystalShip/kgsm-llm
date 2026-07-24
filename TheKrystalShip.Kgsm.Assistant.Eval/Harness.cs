using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Drives the REAL assistant in-process — the same three-call backend the CLI wires — so the
/// trajectory it scores is the one a user would get. Reads each turn's tool trajectory back from the
/// canonical conversation store (the trajectory now lives in the history, not a side recorder),
/// isolated to a throwaway SQLite DB so an eval run never touches the user's real transcript corpus.
/// <para>
/// HARD INVARIANT — non-destructive: the harness only ever calls <see cref="IServerAssistant.RunAsync"/>,
/// which STAGES destructive ops; it never calls <c>ConfirmAsync</c>, so nothing it runs can start,
/// stop, install, or delete a server. There is no code path here to execution.
/// </para>
/// </summary>
internal sealed class Harness
{
    private readonly ServiceProvider _provider;
    private readonly IConversationStore _store;
    private readonly EvalOptions _options;

    private Harness(ServiceProvider provider, IConversationStore store, EvalOptions options)
    {
        _provider = provider;
        _store = store;
        _options = options;
    }

    /// <summary>Test seam: resolve a wired backend service to assert DI composition — e.g. that the
    /// unified <c>search</c> tool is offered in the eval — without a model or a live kgsm.</summary>
    internal T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    public static Harness Build(EvalOptions options)
    {
        var config = BuildConfiguration(options);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
            b.AddSimpleConsole();
            // Keep stdout for the scorecard alone — route every backend log line to stderr.
            b.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        services.AddLocalLlm(config);      // Ollama client, conversation store, agent loop, recorder
        services.AddKgsmAssistant();       // prompt builder, dispatcher, policy, IServerAssistant
        services.AddKgsmAdapters(config);  // kgsm-lib graph + ports + (unconfigured) web search

        // The eval scores ROUTING, so the unified `search` tool must be OFFERED even though no real
        // source is wired here (Rag off, no Tavily key) — otherwise the E-group search rubrics would
        // be unsatisfiable (CalledTool(search)) or pass vacuously (DidNotCallTool(search)). Force
        // availability LAST (after AddKgsmAdapters' PostConfigure); the underlying ports stay disabled,
        // so an actual call just returns an honest "couldn't search" — no network, no credits spent.
        services.PostConfigure<SearchOptions>(o => o.WebEnabled = true);
        // Same reasoning for fetch_url (F-group): offered even though WebFetch:Enabled is unset here,
        // so both CalledTool(fetch_url) and DidNotCallTool(fetch_url) are meaningful. AddKgsmAdapters
        // registers the real HttpWebFetch unconditionally (mirroring TavilyWebSearch), but its own
        // WebFetch:Enabled stays false, so an actual call just returns an honest "not configured" — no
        // network, no host ever contacted.
        services.PostConfigure<FetchOptions>(o => o.Available = true);
        // Same reasoning for create_blueprint (B-group): offered even though BlueprintAuthoring:Enabled
        // is unset here, so CalledTool(create_blueprint) is meaningful. AddKgsmAdapters registers the
        // real BlueprintAuthoringAggregator unconditionally, but its own Enabled flag stays false, so a
        // model-routed call here reports itself as "not configured" and never touches kgsm-lib's
        // write-side blueprint/instance authorities — no install, no uninstall, no file write.
        services.PostConfigure<BlueprintAuthoringFlags>(o => o.Available = true);

        // The conversation store (SQLite) is already registered by AddLocalLlm and pointed at a throwaway
        // temp DB (see BuildConfiguration) so eval turns never touch the user's real corpus. The harness
        // reads each turn's trajectory back from it.
        var provider = services.BuildServiceProvider();
        return new Harness(provider, provider.GetRequiredService<IConversationStore>(), options);
    }

    /// <summary>Resolve fixtures and print the preflight. Returns null if the run must abort (empty inventory).</summary>
    public async Task<ResolvedFixtures?> PreflightAsync(CancellationToken ct)
    {
        var inventory = _provider.GetRequiredService<IServerInventory>();
        var ops = _provider.GetRequiredService<IServerOperations>();
        var fx = await Fixtures.ResolveAsync(inventory, ops, ct);
        return Fixtures.Preflight(fx, Console.Error) ? fx : null;
    }

    public async Task<EvalRun> RunAsync(ResolvedFixtures fx, IReadOnlyList<BenchmarkCase> cases, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var assistant = _provider.GetRequiredService<IServerAssistant>();
        var invocation = _provider.GetRequiredService<IInvocationContext>();
        using var scope = invocation.Begin(Invocation.ForCli(Environment.UserName));

        var caseResults = new List<CaseResultDto>();
        string? sysHash = null;

        foreach (var bench in cases)
        {
            var missing = bench.RequiredRoles.Where(r => !fx.Has(r)).ToList();
            if (missing.Count > 0)
            {
                var reason = "missing fixture role(s): " + string.Join(", ", missing.Select(r => r.ToString()));
                Console.Error.WriteLine($"  · {bench.Id,-4} SKIP — {reason}");
                caseResults.Add(new CaseResultDto(bench.Id, bench.Title, bench.Authorized, true, reason,
                    Array.Empty<RepResultDto>(), Array.Empty<CheckSummaryDto>(), new Dictionary<string, double>()));
                continue;
            }

            var reps = new List<RepResultDto>();
            for (var rep = 1; rep <= _options.Reps; rep++)
            {
                ct.ThrowIfCancellationRequested();
                Console.Error.Write($"  · {bench.Id,-4} rep {rep}/{_options.Reps} … ");
                var (repResult, hash) = await RunRepAsync(assistant, bench, fx, rep, ct);
                sysHash ??= hash;
                reps.Add(repResult);
                Console.Error.WriteLine(SummariseRep(repResult));
            }

            caseResults.Add(Summarise(bench, reps));
        }

        var summary = AggregateDimensions(caseResults);
        var overall = summary.Where(s => s.Covered).Sum(s => s.Passed) is var p && summary.Where(s => s.Covered).Sum(s => s.Total) is var t && t > 0
            ? (double)p / t : 0.0;

        return new EvalRun(
            EvalRun.CurrentSchema, BenchmarkSuite.Version, startedAt, DateTimeOffset.UtcNow,
            _options.Model, _options.Temperature, _options.Seed, _options.Reps, sysHash,
            new HostInfoDto(fx.Instances.Count, fx.RunningCount, fx.StoppedCount, fx.Blueprints.Count, new Dictionary<string, string?>
            {
                ["unique_game"] = fx.UniqueGameInstance is null ? null : $"{fx.UniqueGameWord} → {fx.UniqueGameInstance}",
                ["running"] = fx.RunningInstance,
                ["stopped"] = fx.StoppedInstance,
                ["never_game"] = fx.NeverInstalledGame,
            }),
            caseResults, summary, overall);
    }

    private async Task<(RepResultDto, string? sysHash)> RunRepAsync(
        IServerAssistant assistant, BenchmarkCase bench, ResolvedFixtures fx, int rep, CancellationToken ct)
    {
        // Fresh conversation per rep so context never bleeds between reps or cases (order-independence).
        var conversationId = $"eval:{bench.Id}:r{rep}:{Guid.NewGuid():N}";
        var steps = new List<StepResultDto>();
        string? sysHash = null;

        for (var si = 0; si < bench.Steps.Count; si++)
        {
            var step = bench.Steps[si];
            var prompt = fx.Fill(step.Prompt);

            // autoExecute: false — the harness is non-destructive by construction (CLAUDE.md inv. #2):
            // RunAsync only STAGES; it must never run a server op, so auto-accept stays off here.
            var result = await assistant.RunAsync(conversationId, prompt, bench.Authorized, false, autoExecute: false, requestedTools: null, ct);
            // Read this step's turn back from the canon store (fresh conversation per rep → the last turn
            // entry is this step's). The trajectory now lives in the history, not a side recorder.
            var record = _store.GetHistory(conversationId)
                .LastOrDefault(e => e.Kind == ConversationEntryKind.Turn)?.Turn;
            sysHash ??= record?.SystemPromptHash;

            var obs = new TurnObservation(
                prompt,
                record?.Tools ?? Array.Empty<RecordedToolCall>(),
                result.IsSuccess ? result.Confirmations : Array.Empty<PendingConfirmation>(),
                record?.Iterations ?? 0,
                record?.Outcome ?? TurnOutcome.Error,
                result.IsSuccess ? result.Text : record?.Final ?? "");

            var checks = step.Checks
                .Select(c => new CheckResultDto(c.Label, c.Dimension.ToString(), c.Evaluate(obs, fx)))
                .ToList();

            steps.Add(new StepResultDto(
                si, prompt,
                obs.Tools.Select(FormatTool).ToList(),
                obs.Staged.Select(FormatStaged).ToList(),
                obs.Iterations,
                obs.Outcome.ToString(),
                obs.Final.Trim(),   // full reply, kept for transcript reading / live judging
                checks));
        }

        return (new RepResultDto(rep, steps), sysHash);
    }

    // --- scoring -----------------------------------------------------------------------------------

    private static CaseResultDto Summarise(BenchmarkCase bench, IReadOnlyList<RepResultDto> reps)
    {
        var summaries = new List<CheckSummaryDto>();
        var dimAgg = new Dictionary<string, (int pass, int total)>();

        for (var si = 0; si < bench.Steps.Count; si++)
        {
            var checks = bench.Steps[si].Checks;
            for (var pos = 0; pos < checks.Count; pos++)
            {
                var check = checks[pos];
                var passed = reps.Count(r => r.Steps[si].Checks[pos].Pass);
                var total = reps.Count;
                var dim = check.Dimension.ToString();
                summaries.Add(new CheckSummaryDto($"s{si}:{check.Label}", check.Label, dim, passed, total,
                    total == 0 ? 0 : (double)passed / total));

                var (p, t) = dimAgg.GetValueOrDefault(dim);
                dimAgg[dim] = (p + passed, t + total);
            }
        }

        var dims = dimAgg.ToDictionary(kv => kv.Key, kv => kv.Value.total == 0 ? 0 : (double)kv.Value.pass / kv.Value.total);
        return new CaseResultDto(bench.Id, bench.Title, bench.Authorized, false, null, reps, summaries, dims);
    }

    private static IReadOnlyList<DimensionSummaryDto> AggregateDimensions(IReadOnlyList<CaseResultDto> cases)
    {
        var agg = new Dictionary<string, (int pass, int total)>();
        foreach (var c in cases.Where(c => !c.Skipped))
            foreach (var s in c.Checks)
            {
                var (p, t) = agg.GetValueOrDefault(s.Dimension);
                agg[s.Dimension] = (p + s.Passed, t + s.Total);
            }

        // Every rubric dimension appears, even F (uncovered → Covered=false), so the scorecard is honest
        // about what is and isn't auto-scored.
        return Enum.GetNames<Rubric>().Select(dim =>
        {
            var (p, t) = agg.GetValueOrDefault(dim);
            return new DimensionSummaryDto(dim, p, t, t == 0 ? 0 : (double)p / t, t > 0);
        }).ToList();
    }

    // --- formatting --------------------------------------------------------------------------------

    private static string SummariseRep(RepResultDto rep)
    {
        var allChecks = rep.Steps.SelectMany(s => s.Checks).ToList();
        var passed = allChecks.Count(c => c.Pass);
        var tools = rep.Steps.SelectMany(s => s.Tools).ToList();
        var staged = rep.Steps.SelectMany(s => s.Staged).ToList();
        var trail = tools.Count == 0 && staged.Count == 0 ? "(no tools)" : string.Join(" ", tools.Concat(staged.Select(s => $"⏸{s}")));
        return $"{passed}/{allChecks.Count} checks · {trail}";
    }

    private static string FormatTool(RecordedToolCall t)
    {
        var args = t.Arguments.Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .Select(a => $"{a.Key}={a.Value}");
        return $"{t.Name}({string.Join(",", args)})";
    }

    private static string FormatStaged(PendingConfirmation c) =>
        $"{c.Kind}({c.InstanceName ?? c.Target})";

    // --- configuration -----------------------------------------------------------------------------

    private static IConfiguration BuildConfiguration(EvalOptions o)
    {
        var builder = new ConfigurationBuilder()
            // 1. The eval's own defaults (Ollama endpoint, Recording off), shipped next to the binary.
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            // 2. The operator's CLI config — inherits KGSM:Path and Ollama:Endpoint so the eval "just
            //    works" on a box where the assistant CLI is already set up.
            .AddJsonFile(CliUserConfigPath(), optional: true)
            // 3. Environment (KGSM__Path, Ollama__Endpoint, …).
            .AddEnvironmentVariables();

        var overrides = new Dictionary<string, string?>
        {
            // Point the conversation store at a throwaway temp DB so eval turns never touch the user's
            // real corpus (the store is always-on now — there is no "recording off" switch).
            ["Conversation:DatabasePath"] =
                Path.Combine(Path.GetTempPath(), $"kgsm-eval-conv-{Guid.NewGuid():N}.db"),
            ["Ollama:Model"] = o.Model,
            ["Ollama:Temperature"] = o.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Default the prompt dir to the SAME files the CLI tunes, so the eval measures the live
            // tuned prompts; an explicit --prompts (e.g. an empty dir) tests the shipped defaults.
            ["Prompts:Directory"] = o.PromptsDir ?? DefaultPromptsDir(),
        };
        if (o.Seed is int seed) overrides["Ollama:Seed"] = seed.ToString();
        if (!string.IsNullOrWhiteSpace(o.Endpoint)) overrides["Ollama:Endpoint"] = o.Endpoint;
        if (!string.IsNullOrWhiteSpace(o.KgsmPath)) overrides["KGSM:Path"] = o.KgsmPath;

        builder.AddInMemoryCollection(overrides);
        return builder.Build();
    }

    public static string? ResolveKgsmPath(EvalOptions o)
    {
        var cfg = BuildConfiguration(o);
        return cfg["KGSM:Path"];
    }

    private static string CliUserConfigPath() =>
        Path.Combine(ConfigHome(), "kgsm-assistant", "appsettings.json");

    private static string DefaultPromptsDir() =>
        Path.Combine(ConfigHome(), "kgsm-assistant", "prompts");

    private static string ConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }
}
