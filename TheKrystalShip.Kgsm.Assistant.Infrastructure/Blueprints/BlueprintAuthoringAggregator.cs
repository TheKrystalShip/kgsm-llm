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

            // Steps 7-10: test-install, verify, and ALWAYS tear down — guaranteed, not best-effort.
            var installAttempted = false;
            try
            {
                var (actor, origin) = Provenance();
                installAttempted = true;
                _progress.Report(LlmTools.CreateBlueprint, "install", "Test-installing a copy to try it out…");
                KgsmResult installResult = await Task.Run(
                    () => _instances.Install(slug, null, null, probeName, actor, origin, null, start: true),
                    cancellationToken);

                if (!installResult.IsSuccess)
                {
                    verifyLog.Add($"attempt {attempt}: test-install failed ({installResult.Stderr})");
                }
                else
                {
                    _progress.Report(LlmTools.CreateBlueprint, "verify", "Booting it up and waiting for it to answer…");
                    (verified, proofLine) = await VerifyAsync(probeName, draft, cancellationToken);
                    verifyLog.Add(verified
                        ? $"attempt {attempt}: verified — {proofLine}"
                        : $"attempt {attempt}: did not verify booting + listening within the timeout");
                }
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

            if (!verified && attempt >= maxAttempts)
                _files.Remove(slug); // exhausted — catalog stays clean
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

    private async Task<(bool Verified, string? ProofLine)> VerifyAsync(
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

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = await _operations.GetHealthSnapshotAsync(probeName, cancellationToken);
            if (health.IsSuccess)
            {
                var snap = health.Value!;
                var regexMatched = successRegex is not null && snap.RecentLogLines.Any(l => successRegex.IsMatch(l));
                var portsUp = snap.PortsReachable == true;

                if (snap.Running && (portsUp || regexMatched))
                {
                    var proof = portsUp
                        ? "it booted and is listening on its configured port"
                        : "it booted and its startup message appeared in the logs";
                    return (true, proof);
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return (false, null);
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
            new("steam_app_id", Value("steam_app_id"), Value("steam_app_id") is null ? null : Source("steam_app_id")),
            new("ports", Value("ports"), Value("ports") is null ? null : Source("ports")),
        };

        var executableFile = Value("executable_file");
        if (string.IsNullOrWhiteSpace(executableFile))
            return (null, provenance); // the one strictly-required native field — never fabricated

        var steamAppId = int.TryParse(Value("steam_app_id"), out var appId) ? appId : 0;
        var portNumber = Value("ports");
        var ports = string.IsNullOrWhiteSpace(portNumber)
            ? string.Empty
            : $"{portNumber}:{portNumber}/tcp|{portNumber}:{portNumber}/udp";

        var draft = new NativeBlueprintDraft
        {
            Name = slug,
            Metadata = new NativeBlueprintMetadataDraft { DisplayName = findings.DisplayName ?? game },
            Native = new NativeBlueprintNativeDraft
            {
                ExecutableFile = executableFile,
                Ports = ports,
                SteamAppId = steamAppId,
            },
        };

        return (draft, provenance);
    }

    private static List<BlueprintFieldProvenance> ToProvenance(BlueprintResearchFindings findings) =>
        findings.Fields.Select(f => new BlueprintFieldProvenance(f.Name, f.Value, f.SourceUrl)).ToList();

    /// <summary>Lowercase kgsm-safe slug (<c>[a-z0-9]+(?:[-_][a-z0-9]+)*</c>, ≤64 chars) — the same shape
    /// <c>IBlueprintFiles</c> requires. A game name that reduces to nothing safe (e.g. all punctuation)
    /// yields an empty string, handled by the caller as "no safe name".</summary>
    private static string Slugify(string game)
    {
        var sb = new StringBuilder();
        var lastWasSeparator = true; // suppress a leading separator
        foreach (var c in game.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sb.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = sb.ToString().Trim('-', '_');
        return slug.Length > 64 ? slug[..64].TrimEnd('-', '_') : slug;
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
        sb.AppendLine($"ports: {(string.IsNullOrEmpty(draft.Native.Ports) ? "null" : draft.Native.Ports)}");
        sb.AppendLine($"steam_app_id: {draft.Native.SteamAppId}");
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
