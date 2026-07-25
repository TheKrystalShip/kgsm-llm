using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Blueprints;

/// <summary>
/// The real <see cref="IBlueprintAuthoring"/> — the full research→draft→persist→install→verify→
/// self-repair→teardown→keep/stash pipeline (<c>assistant-blueprint-authoring-plan.md</c>, "The
/// pipeline"). Lives in Infrastructure (not the Assistant domain project, unlike
/// <c>SearchAggregator</c>) because it needs kgsm-lib's write-side authorities directly
/// (<see cref="IBlueprintFiles"/>, <see cref="IInstanceService"/>, <see cref="IBlueprintService"/>) —
/// the same layering reason <see cref="KgsmServerOperations"/> lives here rather than in the domain
/// project. The <see cref="IBlueprintAuthoring"/> port is what keeps <c>ToolDispatcher</c> decoupled
/// from that dependency.
/// <para>
/// EVAL/SAFETY GATE: the very first thing <see cref="AuthorAsync"/> does is check
/// <see cref="BlueprintAuthoringOptions.Enabled"/> (false everywhere by default) and return an honest
/// "not configured" result WITHOUT touching any kgsm-lib authority when it's off. This is also what
/// makes the tool safe to force-OFFER in the eval harness (mirroring <c>SearchOptions.WebEnabled</c> /
/// <c>FetchOptions.Available</c>): the harness never sets <c>BlueprintAuthoring:Enabled</c>, so a
/// model-routed call in an eval run exercises real DI wiring but never reaches kgsm-lib.
/// </para>
/// </summary>
internal sealed class BlueprintAuthoringAggregator : IBlueprintAuthoring
{
    private readonly BlueprintAuthoringOptions _options;
    private readonly IBlueprintResearch _research;
    private readonly IBlueprintFiles _files;
    private readonly IInstanceFiles _instanceFiles;
    private readonly IBlueprintRepair _repair;
    private readonly IBlueprintService _blueprints;
    private readonly IInstanceService _instances;
    private readonly IServerOperations _operations;
    private readonly IInventoryInvalidation _invalidation;
    private readonly IBlueprintAttemptStore _attempts;
    private readonly IInvocationContext _invocation;
    private readonly ITurnProgress _progress;
    private readonly ILogger<BlueprintAuthoringAggregator> _logger;

    public BlueprintAuthoringAggregator(
        IOptions<BlueprintAuthoringOptions> options,
        IBlueprintResearch research,
        IBlueprintFiles files,
        IInstanceFiles instanceFiles,
        IBlueprintRepair repair,
        IBlueprintService blueprints,
        IInstanceService instances,
        IServerOperations operations,
        IInventoryInvalidation invalidation,
        IBlueprintAttemptStore attempts,
        IInvocationContext invocation,
        ITurnProgress progress,
        ILogger<BlueprintAuthoringAggregator> logger)
    {
        _options = options.Value;
        _research = research;
        _files = files;
        _instanceFiles = instanceFiles;
        _repair = repair;
        _blueprints = blueprints;
        _instances = instances;
        _operations = operations;
        _invalidation = invalidation;
        _attempts = attempts;
        _invocation = invocation;
        _progress = progress;
        _logger = logger;
    }

    private (string? Actor, string? Origin) Provenance()
    {
        Invocation? inv = _invocation.Current;
        return (inv?.Actor, inv?.Origin);
    }

    public async Task<ToolResult<BlueprintAuthoringData>> AuthorAsync(string game, CancellationToken cancellationToken = default)
    {
        game = game.Trim();
        var subject = new ResultRef(ResourceKind.Blueprint, game);

        // --- Gate 0: disabled ------------------------------------------------------------------
        if (!_options.Enabled)
        {
            var summary = $"Automatic blueprint authoring isn't enabled on this host, so I can't research " +
                           $"and build a config for \"{game}\" myself.";
            return Envelope(subject, summary, BlueprintAuthoringOutcome.Disabled, game, null, [], null, summary, false);
        }

        var slug = Slugify(game);
        if (string.IsNullOrEmpty(slug))
        {
            var summary = $"\"{game}\" doesn't give me a safe name to build a blueprint from.";
            return Envelope(subject, summary, BlueprintAuthoringOutcome.NotFeasible, game, null, [], null, summary, false);
        }

        // From here on the slug is the canonical id for what this run is about — the subject used by
        // every outcome below (including the terminal `Verified` card the web install-handoff reads
        // `subject.id` off of to POST /servers {blueprint: slug}).
        subject = new ResultRef(ResourceKind.Blueprint, slug);

        // --- Step 1: existence guard -------------------------------------------------------------
        // GetInfo checks BOTH the system and user blueprint directories — the engine stays the source
        // of truth for "does this already exist", not a possibly-stale inventory cache.
        Blueprint? existing;
        try { existing = await Task.Run(() => _blueprints.GetInfo(slug), cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Existence check failed for blueprint \"{Slug}\"", slug);
            existing = null;
        }
        if (existing is not null)
        {
            var summary = $"\"{game}\" is already in the catalog (as \"{slug}\") — nothing to build.";
            return Envelope(subject, summary, BlueprintAuthoringOutcome.AlreadyExists, game, slug, [], null, null, true);
        }

        // --- Step 2: research (provenance-tagged) -------------------------------------------------
        _progress.Report(LlmTools.CreateBlueprint, "research", $"Looking up \"{game}\" online…");
        var findings = await _research.ResearchAsync(game, cancellationToken);

        // --- Step 3: feasibility gates -------------------------------------------------------------
        _progress.Report(LlmTools.CreateBlueprint, "feasibility", "Checking it can run on Linux…");
        if (findings.Feasibility != BlueprintFeasibility.Feasible)
        {
            var reason = findings.Feasibility switch
            {
                BlueprintFeasibility.NotSelfHostable => $"\"{game}\" doesn't appear to be self-hostable — there's no dedicated/community server for it.",
                BlueprintFeasibility.NoNativeLinuxServer => $"\"{game}\" doesn't have a native Linux dedicated server that I can set up automatically.",
                BlueprintFeasibility.RequiresSteamAccount => $"\"{game}\"'s server files download only through a Steam account that owns the game, so I can't automatically download and test-run it to confirm a working config. You can still add it manually with an account that owns \"{game}\".",
                _ => $"I couldn't find enough online to confirm \"{game}\" can be self-hosted on Linux.",
            };
            await StashAsync(game, null, findings, [], BlueprintAuthoringOutcome.NotFeasible, reason, null, cancellationToken);
            return Envelope(subject, reason, BlueprintAuthoringOutcome.NotFeasible, game, null,
                ToProvenance(findings), null, reason, false);
        }

        // --- Step 4: draft (sourced fields only — unknowns stay null/default) ---------------------
        _progress.Report(LlmTools.CreateBlueprint, "draft", "Building a server config…");
        var (draft, provenance) = BuildDraft(slug, game, findings);
        if (draft is null)
        {
            var reason = $"I found that \"{game}\" can be self-hosted on Linux, but couldn't pin down exactly " +
                          "how its server is launched from what I found online, so I can't build a working config for it.";
            await StashAsync(game, slug, findings, [], BlueprintAuthoringOutcome.Failed, reason, null, cancellationToken);
            return Envelope(subject, reason, BlueprintAuthoringOutcome.Failed, game, null, provenance, null, reason, false);
        }

        // --- Steps 5-10: persist → validate → test-install → verify → self-repair → teardown -----
        var probeName = BlueprintProbeNaming.ForSlug(slug);
        var verifyLog = new List<string>();
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        bool verified = false;
        string? proofLine = null;

        for (var attempt = 1; attempt <= maxAttempts && !verified; attempt++)
        {
            verifyLog.Add($"attempt {attempt}/{maxAttempts}: persisting draft");

            var create = _files.Create(draft, overwrite: attempt > 1);
            if (create.Outcome != FileOpOutcome.Ok)
            {
                verifyLog.Add($"attempt {attempt}: persist failed ({create.Outcome}: {create.Message})");
                _files.Remove(slug);
                _invalidation.Invalidate();
                break; // a persist failure (bad jail, dir unavailable, io error) won't fix itself on retry
            }
            _invalidation.Invalidate();

            // Step 6: validate by reading back through the engine — it stays the schema authority.
            Blueprint? readback;
            try { readback = await Task.Run(() => _blueprints.GetInfo(slug), cancellationToken); }
            catch (Exception ex)
            {
                verifyLog.Add($"attempt {attempt}: readback threw ({ex.Message})");
                readback = null;
            }
            if (readback is null)
            {
                verifyLog.Add($"attempt {attempt}: readback validation failed — the engine rejected the draft");
                _files.Remove(slug);
                _invalidation.Invalidate();
                break; // never install an unvalidated draft
            }

            // Steps 7-10: test-install, verify, gather evidence for repair, and ALWAYS tear down.
            var installAttempted = false;
            var installSucceeded = false;
            string? installError = null;
            InstanceHealthSnapshot? lastSnapshot = null;
            RepairEvidence? evidence = null;
            try
            {
                var (actor, origin) = Provenance();
                installAttempted = true;
                _progress.Report(LlmTools.CreateBlueprint, "install", "Test-installing a copy to try it out…");
                KgsmResult installResult = await Task.Run(
                    () => _instances.Install(slug, null, null, probeName, actor, origin, null, start: true),
                    cancellationToken);

                installSucceeded = installResult.IsSuccess;
                if (!installSucceeded)
                {
                    installError = installResult.Stderr;
                    verifyLog.Add($"attempt {attempt}: test-install failed ({installResult.Stderr})");
                }
                else
                {
                    _progress.Report(LlmTools.CreateBlueprint, "verify", "Booting it up and waiting for it to answer…");
                    (verified, proofLine, lastSnapshot) = await VerifyAsync(probeName, draft, cancellationToken);
                    verifyLog.Add(verified
                        ? $"attempt {attempt}: verified — {proofLine}"
                        : $"attempt {attempt}: did not verify booting + listening within the timeout");
                }

                // Gather the ground-truth evidence WHILE the probe's files are still on disk (before the
                // finally block tears them down): the real install tree, the shipped launch scripts, and
                // the boot log. Only when we're going to repair (not verified, attempts remain).
                if (!verified && attempt < maxAttempts)
                    evidence = GatherEvidence(probeName, installSucceeded, installError, lastSnapshot, cancellationToken);
            }
            catch (Exception ex)
            {
                verifyLog.Add($"attempt {attempt}: install/verify threw ({ex.Message})");
            }
            finally
            {
                if (installAttempted)
                {
                    _progress.Report(LlmTools.CreateBlueprint, "teardown", "Cleaning up the test copy…");
                    await TeardownAsync(probeName);
                }
            }

            if (verified)
                break;

            if (attempt >= maxAttempts)
            {
                _files.Remove(slug); // exhausted — catalog stays clean
                _invalidation.Invalidate();
                break;
            }

            // B — evidence-driven repair: feed the real install tree + boot log to the model and let it
            // propose corrected launch fields. A null result means "no better idea than this draft" — stop
            // rather than burn the remaining attempts re-running an identical config.
            _progress.Report(LlmTools.CreateBlueprint, "repair", "Reading what actually installed to fix the config…");
            var (repairedDraft, repairNote) = await TryRepairAsync(game, draft, evidence, cancellationToken);
            if (repairedDraft is null)
            {
                verifyLog.Add($"attempt {attempt}: no evidence-driven repair available — stopping ({repairNote})");
                _files.Remove(slug);
                _invalidation.Invalidate();
                break;
            }

            verifyLog.Add($"attempt {attempt}: repaired draft from install evidence — {repairNote}");
            draft = repairedDraft;
        }

        if (verified)
        {
            _invalidation.Invalidate();
            var summary = $"I didn't have \"{game}\", so I researched it, built a config, and test-ran it — " +
                           $"{proofLine}. **{game} is now in the catalog.** Want me to make you a server?";
            return Envelope(subject, summary, BlueprintAuthoringOutcome.Verified, game, slug, provenance, proofLine, null, true);
        }

        _invalidation.Invalidate();
        var draftYaml = RenderForStash(draft);
        await StashAsync(game, slug, findings, verifyLog, BlueprintAuthoringOutcome.Failed,
            "test-install never verified booting and listening within the self-repair bound", draftYaml, cancellationToken);

        var failSummary = $"I researched \"{game}\" and tried to build and test a config for it, but couldn't " +
                           "get it to boot and answer on its port — so I didn't add it to the catalog.";
        return Envelope(subject, failSummary, BlueprintAuthoringOutcome.Failed, game, null, provenance, null, failSummary, false);
    }

    // --- verify: boots + listens ------------------------------------------------------------------

    private async Task<(bool Verified, string? ProofLine, InstanceHealthSnapshot? LastSnapshot)> VerifyAsync(
        string probeName, NativeBlueprintDraft draft, CancellationToken cancellationToken)
    {
        System.Text.RegularExpressions.Regex? successRegex = null;
        if (!string.IsNullOrWhiteSpace(draft.Native.StartupSuccessRegex))
        {
            try
            {
                successRegex = new System.Text.RegularExpressions.Regex(
                    draft.Native.StartupSuccessRegex, System.Text.RegularExpressions.RegexOptions.None,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                successRegex = null; // an unparsable sourced regex is simply not usable as a signal
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _options.VerifyTimeoutSeconds));
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.VerifyPollIntervalSeconds));
        InstanceHealthSnapshot? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = await _operations.GetHealthSnapshotAsync(probeName, cancellationToken);
            if (health.IsSuccess)
            {
                var snap = health.Value!;
                // Keep the most log-bearing snapshot as the evidence handed to repair on failure: prefer a
                // later snapshot, but never let an empty-log poll overwrite one that captured real output.
                if (last is null || snap.RecentLogLines.Count >= last.RecentLogLines.Count)
                    last = snap;

                var regexMatched = successRegex is not null && snap.RecentLogLines.Any(l => successRegex.IsMatch(l));
                var portsUp = snap.PortsReachable == true;

                if (snap.Running && (portsUp || regexMatched))
                {
                    var proof = portsUp
                        ? "it booted and is listening on its configured port"
                        : "it booted and its startup message appeared in the logs";
                    return (true, proof, snap);
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return (false, null, last);
    }

    // --- A + B: gather ground-truth evidence, then repair the draft from it --------------------------

    /// <summary>What the repair step reads instead of guessing from the web: the real installed tree, the
    /// game's own shipped launch scripts, and the boot log of the failed attempt.</summary>
    private sealed record RepairEvidence(
        bool InstallSucceeded, string? InstallError, string InstallTree, string LaunchScripts,
        string BootLog, bool? PortsReachable);

    // The instance's game files land under <working_dir>/install/[executable_subdirectory]/ — IInstanceFiles
    // jails to <working_dir>, so the install tree is listed from the "install" subdir and every path the
    // model sees (and every executable_subdirectory it proposes) is relative to that root.
    private const string InstallRoot = "install";
    private const int MaxTreeEntries = 300;   // total entries walked into the rendered tree
    private const int MaxTreeDirs = 60;       // directories descended into (breadth bound)
    private const int MaxScriptFiles = 6;     // shipped *.sh scripts read in full
    private const long MaxScriptBytes = 8192; // per-script read ceiling
    private const int MaxLogFiles = 3;        // game-written log files read for readiness evidence
    private const long MaxLogBytes = 32768;   // per-log read ceiling (the ready line prints early)

    /// <summary>Reads the probe's real install directory and boot log into a <see cref="RepairEvidence"/>.
    /// Best-effort: any read failure degrades to an empty section, never throws (except cancellation).</summary>
    private RepairEvidence GatherEvidence(
        string probeName, bool installSucceeded, string? installError,
        InstanceHealthSnapshot? lastSnapshot, CancellationToken cancellationToken)
    {
        var tree = new StringBuilder();
        var scriptPaths = new List<string>();
        var logPaths = new List<string>();
        try
        {
            WalkInstallTree(probeName, tree, scriptPaths, logPaths, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Blueprint-repair evidence: listing the install tree failed for {Probe}", probeName);
        }

        var scripts = new StringBuilder();
        foreach (var rel in scriptPaths.Take(MaxScriptFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var read = _instanceFiles.Read(probeName, rel, MaxScriptBytes);
                if (read.IsOk && read.Value is not null)
                    scripts.Append("=== ").Append(rel).Append(" ===\n").Append(read.Value.Content).Append("\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Blueprint-repair evidence: reading script {Path} failed for {Probe}", rel, probeName);
            }
        }

        // The boot log kgsm captured (the instance's own stdout .log). This is what BOTH the verify check
        // and repair read — but a game that redirects its output to its own file (e.g. Unity's `-logfile`)
        // leaves it empty. So supplement it with any log files the game itself wrote into the install dir:
        // the readiness line ("Game ID", "Server started") is in there even when kgsm's capture is blank,
        // which is what lets repair synthesize a startup_success_regex AND learn to drop the redirect.
        var bootLog = new StringBuilder();
        if (lastSnapshot is not null && lastSnapshot.RecentLogLines.Count > 0)
            bootLog.Append("--- monitored stdout (what the readiness check sees) ---\n")
                   .Append(string.Join("\n", lastSnapshot.RecentLogLines)).Append('\n');

        foreach (var rel in logPaths.Take(MaxLogFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var read = _instanceFiles.Read(probeName, rel, MaxLogBytes);
                if (read.IsOk && read.Value is not null && !string.IsNullOrWhiteSpace(read.Value.Content))
                    bootLog.Append("--- game-written log file ").Append(rel)
                           .Append(" (NOT on monitored stdout — readiness here is invisible to the monitor) ---\n")
                           .Append(read.Value.Content).Append('\n');
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Blueprint-repair evidence: reading log {Path} failed for {Probe}", rel, probeName);
            }
        }

        return new RepairEvidence(installSucceeded, installError, tree.ToString(), scripts.ToString(),
            bootLog.ToString(), lastSnapshot?.PortsReachable);
    }

    /// <summary>Bounded breadth-first walk of the install dir: renders one "path (kind, size)" line per
    /// entry (relative to the install root) and collects the relative paths of <c>*.sh</c> scripts and of
    /// game-written log files (the two high-value read lists). Bounds keep a pathological tree (thousands
    /// of files) from blowing the budget.</summary>
    private void WalkInstallTree(
        string probeName, StringBuilder tree, List<string> scriptPaths, List<string> logPaths, CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        queue.Enqueue(string.Empty); // the install root itself
        var entries = 0;
        var dirs = 0;

        while (queue.Count > 0 && entries < MaxTreeEntries && dirs < MaxTreeDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sub = queue.Dequeue();
            dirs++;

            var listPath = string.IsNullOrEmpty(sub) ? InstallRoot : $"{InstallRoot}/{sub}";
            var listing = _instanceFiles.List(probeName, listPath, MaxTreeEntries);
            if (!listing.IsOk || listing.Value is null)
                continue;

            foreach (var entry in listing.Value.Entries)
            {
                if (entries >= MaxTreeEntries)
                    break;
                entries++;

                var rel = string.IsNullOrEmpty(sub) ? entry.Name : $"{sub}/{entry.Name}";
                var size = entry.SizeBytes is { } b ? $", {b} bytes" : string.Empty;
                tree.Append(rel).Append(" (").Append(entry.Kind.ToString().ToLowerInvariant()).Append(size).Append(")\n");

                if (entry.Kind == FileKind.Dir)
                    queue.Enqueue(rel);
                else if (entry.Kind == FileKind.File)
                {
                    if (entry.Name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
                        scriptPaths.Add($"{InstallRoot}/{rel}");
                    else if (IsLogLike(entry.Name))
                        logPaths.Add($"{InstallRoot}/{rel}");
                }
            }
        }
    }

    /// <summary>A file the game likely wrote its own startup output to — a <c>.log</c>, or a
    /// <c>*log*.txt</c> (Unity's <c>-logfile ServerLog.txt</c> is the common shape). Read for the readiness
    /// line the monitored stdout may be missing.</summary>
    private static bool IsLogLike(string name) =>
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
        (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
         name.Contains("log", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the repair model over the gathered evidence and merges its proposal onto the failed
    /// draft. Returns the corrected draft + a short note, or (null, reason) when there is no usable repair
    /// (no evidence, model declined, or the proposal changed nothing) — the signal to stop retrying.</summary>
    private async Task<(NativeBlueprintDraft? Draft, string Note)> TryRepairAsync(
        string game, NativeBlueprintDraft draft, RepairEvidence? evidence, CancellationToken cancellationToken)
    {
        if (evidence is null)
            return (null, "no install evidence was captured");

        var context = new BlueprintRepairContext(
            game,
            draft.Native.ExecutableFile,
            draft.Native.ExecutableSubdirectory,
            draft.Native.ExecutableArguments,
            draft.Native.StartupSuccessRegex,
            draft.Native.Ports,
            evidence.InstallSucceeded,
            evidence.InstallError,
            evidence.InstallTree,
            evidence.LaunchScripts,
            evidence.BootLog,
            evidence.PortsReachable);

        var proposal = await _repair.RepairAsync(context, cancellationToken);
        if (proposal is null)
            return (null, "the repair step had no better idea than the current draft");

        var merged = ApplyRepair(draft, proposal);
        if (RendersIdentically(draft, merged))
            return (null, "the repair proposal did not change any launch field");

        return (merged, DescribeRepair(proposal));
    }

    /// <summary>Merges a repair proposal onto a draft: a non-null proposal field replaces the draft's value;
    /// null leaves it. Ports arrive as a bare number and re-render to the same UFW tcp|udp shape the draft
    /// building step uses. The Steam app ids and metadata are preserved untouched.</summary>
    private static NativeBlueprintDraft ApplyRepair(NativeBlueprintDraft draft, BlueprintRepairProposal p)
    {
        var ports = p.Ports is null
            ? draft.Native.Ports
            : (string.IsNullOrWhiteSpace(p.Ports) ? string.Empty : $"{p.Ports}/tcp|{p.Ports}/udp");

        return draft with
        {
            Native = draft.Native with
            {
                ExecutableFile = p.ExecutableFile ?? draft.Native.ExecutableFile,
                ExecutableSubdirectory = p.ExecutableSubdirectory ?? draft.Native.ExecutableSubdirectory,
                ExecutableArguments = p.ExecutableArguments ?? draft.Native.ExecutableArguments,
                StartupSuccessRegex = p.StartupSuccessRegex ?? draft.Native.StartupSuccessRegex,
                Ports = ports,
            },
        };
    }

    private static bool RendersIdentically(NativeBlueprintDraft a, NativeBlueprintDraft b) =>
        a.Native.ExecutableFile == b.Native.ExecutableFile &&
        a.Native.ExecutableSubdirectory == b.Native.ExecutableSubdirectory &&
        a.Native.ExecutableArguments == b.Native.ExecutableArguments &&
        a.Native.StartupSuccessRegex == b.Native.StartupSuccessRegex &&
        a.Native.Ports == b.Native.Ports;

    private static string DescribeRepair(BlueprintRepairProposal p)
    {
        var parts = new List<string>();
        if (p.ExecutableFile is not null) parts.Add($"executable_file→{p.ExecutableFile}");
        if (p.ExecutableSubdirectory is not null) parts.Add($"executable_subdirectory→{(p.ExecutableSubdirectory.Length == 0 ? "(root)" : p.ExecutableSubdirectory)}");
        if (p.ExecutableArguments is not null) parts.Add($"executable_arguments→{(p.ExecutableArguments.Length == 0 ? "(none)" : p.ExecutableArguments)}");
        if (p.StartupSuccessRegex is not null) parts.Add("startup_success_regex updated");
        if (p.Ports is not null) parts.Add($"ports→{p.Ports}");
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }

    // --- teardown: guaranteed, not best-effort ------------------------------------------------------

    /// <summary>Always uninstalls the probe — called from a <c>finally</c> so it runs on every exit path
    /// (success, verify failure, or an exception). Uses <see cref="CancellationToken.None"/> deliberately:
    /// if the caller's token is what's cancelling this call, teardown must still happen — a cancelled
    /// caller must never leave a probe behind (the startup sweep is the last-resort backstop for a
    /// process crash mid-pipeline, not for this).</summary>
    private async Task TeardownAsync(string probeName)
    {
        try
        {
            var (actor, origin) = Provenance();
            await Task.Run(() => _instances.Uninstall(probeName, actor, origin), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Guaranteed teardown of blueprint-authoring probe {Probe} threw — the startup orphan sweep " +
                "will remove it on next restart if it was left behind", probeName);
        }
    }

    // --- draft building -----------------------------------------------------------------------------

    private static (NativeBlueprintDraft? Draft, List<BlueprintFieldProvenance> Provenance) BuildDraft(
        string slug, string game, BlueprintResearchFindings findings)
    {
        string? Value(string name) => findings.Fields.FirstOrDefault(f => f.Name == name)?.Value;
        string? Source(string name) => findings.Fields.FirstOrDefault(f => f.Name == name)?.SourceUrl;

        var provenance = new List<BlueprintFieldProvenance>
        {
            new("executable_file", Value("executable_file"), Value("executable_file") is null ? null : Source("executable_file")),
            new("executable_subdirectory", Value("executable_subdirectory"), Value("executable_subdirectory") is null ? null : Source("executable_subdirectory")),
            new("executable_arguments", Value("executable_arguments"), Value("executable_arguments") is null ? null : Source("executable_arguments")),
            new("steam_app_id", Value("steam_app_id"), Value("steam_app_id") is null ? null : Source("steam_app_id")),
            new("client_steam_app_id", Value("client_steam_app_id"), Value("client_steam_app_id") is null ? null : Source("client_steam_app_id")),
            new("ports", Value("ports"), Value("ports") is null ? null : Source("ports")),
            new("startup_success_regex", Value("startup_success_regex"), Value("startup_success_regex") is null ? null : Source("startup_success_regex")),
        };

        var executableFile = Value("executable_file");
        if (string.IsNullOrWhiteSpace(executableFile))
            return (null, provenance); // the one strictly-required native field — never fabricated

        var steamAppId = int.TryParse(Value("steam_app_id"), out var appId) ? appId : 0;
        // Ports render in UFW format — "<port>/<proto>", pipe-separated (e.g. 7777/tcp|7777/udp) — the
        // shape kgsm's blueprint schema expects. The extractor sources a single port number; emit both
        // tcp and udp for it (a superset that covers whichever the game actually binds; the verify probe's
        // port-reachability check confirms which is live).
        var portNumber = Value("ports");
        var ports = string.IsNullOrWhiteSpace(portNumber)
            ? string.Empty
            : $"{portNumber}/tcp|{portNumber}/udp";

        var clientAppId = int.TryParse(Value("client_steam_app_id"), out var cAppId) ? cAppId : 0;

        var draft = new NativeBlueprintDraft
        {
            Name = slug,
            Metadata = new NativeBlueprintMetadataDraft { DisplayName = findings.DisplayName ?? game },
            Native = new NativeBlueprintNativeDraft
            {
                ExecutableFile = executableFile,
                ExecutableSubdirectory = Value("executable_subdirectory") ?? string.Empty,
                ExecutableArguments = Value("executable_arguments") ?? string.Empty,
                Ports = ports,
                SteamAppId = steamAppId,
                ClientSteamAppId = clientAppId,
                StartupSuccessRegex = Value("startup_success_regex") ?? string.Empty,
            },
        };

        return (draft, provenance);
    }

    private static List<BlueprintFieldProvenance> ToProvenance(BlueprintResearchFindings findings) =>
        findings.Fields.Select(f => new BlueprintFieldProvenance(f.Name, f.Value, f.SourceUrl)).ToList();

    /// <summary>Lowercase kgsm-safe slug — concatenated ASCII alphanumerics, ≤64 chars, matching the
    /// convention every shipped blueprint follows (<c>abioticfactor</c>, <c>corekeeper</c>,
    /// <c>dontstarvetogether</c> — no separators). Word boundaries are dropped, not hyphenated, so the
    /// existence-guard's slug matches the catalog file name for a game that is already present. A game
    /// name that reduces to nothing safe (e.g. all punctuation) yields an empty string, handled by the
    /// caller as "no safe name".</summary>
    private static string Slugify(string game)
    {
        var sb = new StringBuilder();
        foreach (var c in game.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(c);
        }

        var slug = sb.ToString();
        return slug.Length > 64 ? slug[..64] : slug;
    }

    // --- admin stash ---------------------------------------------------------------------------------

    private async Task StashAsync(
        string game, string? blueprintName, BlueprintResearchFindings findings, IReadOnlyList<string> verifyLog,
        BlueprintAuthoringOutcome outcome, string reason, string? draftYaml, CancellationToken cancellationToken)
    {
        var record = new BlueprintAttemptRecord(
            game, blueprintName, DateTimeOffset.UtcNow, outcome, reason, draftYaml, ToProvenance(findings), verifyLog);
        try
        {
            await _attempts.RecordAsync(record, cancellationToken);
        }
        catch (Exception ex)
        {
            // The stash is a best-effort admin convenience — a failure here must never turn an honest
            // "couldn't do this one" outcome into an error for the user.
            _logger.LogWarning(ex, "Failed to stash blueprint-authoring attempt for \"{Game}\"", game);
        }
    }

    /// <summary>A human-readable rendering of the draft for the stash record — NOT the engine's own YAML
    /// templater (that's <c>IBlueprintFiles</c>'s internal concern); good enough for an admin to read.</summary>
    private static string RenderForStash(NativeBlueprintDraft draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name: {draft.Name}");
        sb.AppendLine($"display_name: {draft.Metadata.DisplayName ?? "null"}");
        sb.AppendLine($"executable_file: {draft.Native.ExecutableFile}");
        sb.AppendLine($"executable_arguments: {(string.IsNullOrEmpty(draft.Native.ExecutableArguments) ? "null" : draft.Native.ExecutableArguments)}");
        sb.AppendLine($"ports: {(string.IsNullOrEmpty(draft.Native.Ports) ? "null" : draft.Native.Ports)}");
        sb.AppendLine($"steam_app_id: {draft.Native.SteamAppId}");
        sb.AppendLine($"startup_success_regex: {(string.IsNullOrEmpty(draft.Native.StartupSuccessRegex) ? "null" : draft.Native.StartupSuccessRegex)}");
        return sb.ToString();
    }

    private static ToolResult<BlueprintAuthoringData> Envelope(
        ResultRef subject, string summary, BlueprintAuthoringOutcome outcome, string game, string? blueprintName,
        IReadOnlyList<BlueprintFieldProvenance> provenance, string? proofLine, string? reason, bool offerInstance) =>
        new(
            LlmTools.CreateBlueprint,
            outcome == BlueprintAuthoringOutcome.Verified ? Confidence.Confirmed : Confidence.Likely,
            subject,
            summary,
            new BlueprintAuthoringData(outcome, game, blueprintName, provenance, proofLine, reason, offerInstance));
}
