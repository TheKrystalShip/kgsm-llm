using System.Diagnostics;
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
    private readonly IAssistantJournal _journal;
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
        IAssistantJournal journal,
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
        _journal = journal;
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
        // The autonomous full run: draft, then finalize back-to-back with no human in the loop
        // (reviewable: false → exhaustion ends at Failed). The mandatory-review flow instead calls
        // DraftAsync (checkpoint) then FinalizeAsync across a confirmation.
        var (terminal, ready) = await DraftCoreAsync(game, cancellationToken);
        return terminal ?? await RunFinalizeAsync(ready!, reviewable: false, cancellationToken);
    }

    public async Task<ToolResult<BlueprintAuthoringData>> DraftAsync(string game, CancellationToken cancellationToken = default)
    {
        // The checkpoint: research + build, then hand the rendered YAML back for the user to review and
        // edit — no test-install. A run that stops before a draft returns that terminal outcome verbatim.
        var (terminal, ready) = await DraftCoreAsync(game, cancellationToken);
        if (terminal is not null)
            return terminal;

        var ctx = ready!;
        var yaml = _files.Render(ctx.Draft);
        var summary = $"I looked up \"{ctx.Game}\" and put together a starting config for it. Review and tweak " +
                       "it below if you like, then save it and I'll test-install it and confirm it boots before adding it.";
        return Envelope(ctx.Subject, summary, BlueprintAuthoringOutcome.DraftReady, ctx.Game, ctx.Slug,
            ctx.Provenance, proofLine: null, reason: null, offerInstance: false,
            draftYaml: yaml, evidence: null, editable: true);
    }

    public async Task<ToolResult<BlueprintAuthoringData>> FinalizeAsync(
        string game, string editedYaml, CancellationToken cancellationToken = default)
    {
        game = game.Trim();

        // --- Gate 0: disabled (same short-circuit as the draft half — touch no write authority) --------
        if (!_options.Enabled)
        {
            var summary = $"Automatic blueprint authoring isn't enabled on this host, so I can't build a config for \"{game}\".";
            var subj = new ResultRef(ResourceKind.Blueprint, game);
            return Envelope(subj, summary, BlueprintAuthoringOutcome.Disabled, game, null, [], null, summary, false);
        }

        // Parse the (user-edited) YAML back into a draft. The engine is still the ultimate validator (the
        // readback inside RunFinalizeAsync), so this parse only structures the text and rejects an edit
        // that isn't a native blueprint / lost its executable_file. An unparseable edit comes back as an
        // editable DraftReady so the user can fix it — never a hard failure they can't recover from.
        var parsed = _files.TryParse(editedYaml);
        if (!parsed.IsOk || parsed.Value is null)
        {
            var slugGuess = Slugify(game);
            var subj = new ResultRef(ResourceKind.Blueprint, string.IsNullOrEmpty(slugGuess) ? game : slugGuess);
            var reason = $"I couldn't read that as a blueprint ({parsed.Message ?? "it isn't valid"}). Fix it and save again.";
            return Envelope(subj, reason, BlueprintAuthoringOutcome.DraftReady, game, null, [], null, reason, false,
                draftYaml: editedYaml, evidence: null, editable: true);
        }

        var draft = parsed.Value;
        var slug = draft.Name; // TryParse enforced a safe slug, and it's the in-file name the engine binds
        var subject = new ResultRef(ResourceKind.Blueprint, slug);

        // Re-validation funnel: a $TOKEN in the launch args that isn't a real $instance_* variable expands
        // to empty at runtime — reject it back to the editor rather than test-install a config that can't boot.
        if (HasForeignPlaceholder(draft.Native.ExecutableArguments))
        {
            var reason = "The launch arguments use a variable KGSM won't fill in — only $instance_* variables are " +
                          "substituted. Remove or fix it and save again.";
            return Envelope(subject, reason, BlueprintAuthoringOutcome.DraftReady, game, slug,
                ProvenanceFromDraft(draft), null, reason, false, draftYaml: editedYaml, evidence: null, editable: true);
        }

        var ctx = new DraftContext(game, slug, subject, draft, ProvenanceFromDraft(draft), EmptyFindings(game));
        return await RunFinalizeAsync(ctx, reviewable: true, cancellationToken);
    }

    public Task<ToolResult<BlueprintAuthoringData>> ReviseAsync(
        string revisedYaml, CancellationToken cancellationToken = default)
    {
        // Conversational refinement of an OPEN, unsaved draft: re-validate the model's revised YAML and
        // hand it straight back as a fresh editable DraftReady — NO test-install (that's still the Save/
        // FinalizeAsync step). Same structural + placeholder funnel FinalizeAsync uses, so a bad edit is
        // recoverable in the editor rather than a hard failure. Never throws (mirrors the port contract).
        if (!_options.Enabled)
        {
            var summary = "Automatic blueprint authoring isn't enabled on this host, so I can't revise a draft here.";
            var subj = new ResultRef(ResourceKind.Blueprint, "blueprint");
            return Task.FromResult(Envelope(subj, summary, BlueprintAuthoringOutcome.Disabled, "the draft", null, [], null, summary, false));
        }

        var parsed = _files.TryParse(revisedYaml);
        if (!parsed.IsOk || parsed.Value is null)
        {
            var subj = new ResultRef(ResourceKind.Blueprint, "blueprint");
            var reason = $"I couldn't read that as a blueprint ({parsed.Message ?? "it isn't valid"}). The draft is unchanged — I left it as it was for you to review.";
            // Hand the text back editable so the open draft is never lost to a bad revision.
            return Task.FromResult(Envelope(subj, reason, BlueprintAuthoringOutcome.DraftReady, "the draft", null, [], null, reason, false,
                draftYaml: revisedYaml, evidence: null, editable: true));
        }

        var draft = parsed.Value;
        var slug = draft.Name;
        var subject = new ResultRef(ResourceKind.Blueprint, slug);
        var display = draft.Metadata?.DisplayName ?? slug;

        if (HasForeignPlaceholder(draft.Native.ExecutableArguments))
        {
            var reason = "The launch arguments use a variable KGSM won't fill in — only $instance_* variables are substituted. Fix it and I'll re-show the draft.";
            return Task.FromResult(Envelope(subject, reason, BlueprintAuthoringOutcome.DraftReady, display, slug,
                ProvenanceFromDraft(draft), null, reason, false, draftYaml: revisedYaml, evidence: null, editable: true));
        }

        // Re-render from the parsed model so the returned YAML is normalized (same shape the first draft
        // used), then re-show it as an editable draft for the user to review and save.
        var yaml = _files.Render(draft);
        var okSummary = $"I've updated the draft for \"{display}\". Review the changes below and save it when you're happy.";
        return Task.FromResult(Envelope(subject, okSummary, BlueprintAuthoringOutcome.DraftReady, display, slug,
            ProvenanceFromDraft(draft), null, null, false, draftYaml: yaml, evidence: null, editable: true));
    }

    /// <summary>Everything a <see cref="RunFinalizeAsync"/> run needs from the draft half: the resolved
    /// display name + canonical slug + subject, the launchable draft, its provenance trail, and the raw
    /// research findings (carried through for the admin stash written on a finalize failure).</summary>
    private sealed record DraftContext(
        string Game,
        string Slug,
        ResultRef Subject,
        NativeBlueprintDraft Draft,
        List<BlueprintFieldProvenance> Provenance,
        BlueprintResearchFindings Findings);

    /// <summary>The draft half of the pipeline: disabled gate → slug → existence guard → research →
    /// feasibility gate → build. Returns a terminal <see cref="ToolResult{TData}"/> when the run stops
    /// before any test-install (disabled, no safe name, already in the catalog, infeasible, or no
    /// launchable draft), otherwise a <see cref="DraftContext"/> ready for <see cref="RunFinalizeAsync"/>.
    /// Touches no kgsm-lib write authority — only the read-only existence check.</summary>
    private async Task<(ToolResult<BlueprintAuthoringData>? Terminal, DraftContext? Ready)> DraftCoreAsync(
        string game, CancellationToken cancellationToken)
    {
        game = game.Trim();
        var subject = new ResultRef(ResourceKind.Blueprint, game);

        // --- Gate 0: disabled ------------------------------------------------------------------
        if (!_options.Enabled)
        {
            var summary = $"Automatic blueprint authoring isn't enabled on this host, so I can't research " +
                           $"and build a config for \"{game}\" myself.";
            return (Envelope(subject, summary, BlueprintAuthoringOutcome.Disabled, game, null, [], null, summary, false), null);
        }

        var slug = Slugify(game);
        if (string.IsNullOrEmpty(slug))
        {
            var summary = $"\"{game}\" doesn't give me a safe name to build a blueprint from.";
            return (Envelope(subject, summary, BlueprintAuthoringOutcome.NotFeasible, game, null, [], null, summary, false), null);
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
            return (Envelope(subject, summary, BlueprintAuthoringOutcome.AlreadyExists, game, slug, [], null, null, true), null);
        }

        // --- Step 2: research (provenance-tagged) -------------------------------------------------
        _progress.Report(ResultCardKinds.BlueprintDraft, "research", $"Looking up \"{game}\" online…");
        var findings = await _research.ResearchAsync(game, cancellationToken);

        // --- Step 3: feasibility gates -------------------------------------------------------------
        _progress.Report(ResultCardKinds.BlueprintDraft, "feasibility", "Checking it can run on Linux…");
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
            return (Envelope(subject, reason, BlueprintAuthoringOutcome.NotFeasible, game, null,
                ToProvenance(findings), null, reason, false), null);
        }

        // --- Step 4: draft (sourced fields only — unknowns stay null/default) ---------------------
        _progress.Report(ResultCardKinds.BlueprintDraft, "draft", "Building a server config…");
        var (draft, provenance) = BuildDraft(slug, game, findings);
        if (draft is null)
        {
            var reason = $"I found that \"{game}\" can be self-hosted on Linux, but couldn't pin down exactly " +
                          "how its server is launched from what I found online, so I can't build a working config for it.";
            await StashAsync(game, slug, findings, [], BlueprintAuthoringOutcome.Failed, reason, null, cancellationToken);
            return (Envelope(subject, reason, BlueprintAuthoringOutcome.Failed, game, null, provenance, null, reason, false), null);
        }

        return (null, new DraftContext(game, slug, subject, draft, provenance, findings));
    }

    /// <summary>The finalize half of the pipeline: persist → validate-by-readback → test-install →
    /// verify → bounded evidence-driven self-repair → guaranteed teardown → keep-or-stash. Runs every
    /// kgsm-lib write authority; only reached once the draft half produced a launchable draft (the
    /// autonomous path) or the user approved/edited one (the review path).
    /// <para><paramref name="reviewable"/> steers what happens when the repair loop exhausts without a
    /// verified boot: the autonomous path (false) ends at <see cref="BlueprintAuthoringOutcome.Failed"/>;
    /// the review path (true) hands the last draft plus its boot evidence back as an editable
    /// <see cref="BlueprintAuthoringOutcome.DraftReady"/> so the user can fix it and save again.</para></summary>
    /// <summary>
    /// Brackets one authoring run in this leaf's journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run installs, starts and uninstalls a real game server, and the engine records every step of
    /// that in full — roughly twenty-five events naming a server that never existed, attributed to
    /// whoever asked a question. What the engine cannot say is that they belong to one run: the probe
    /// naming convention lives here, and no consumer has heard of it. The pair carries the probe name so
    /// a reader can fold those rows into a span instead of reading them as somebody's new server.
    /// </para>
    /// <para>
    /// ⚠ <b>Wrapped rather than hooked at each return.</b> The run below ends at a dozen places — a
    /// persist failure, a readback throw, an exhausted repair loop, success — and a bracket that opens
    /// and does not always close is worse than none: a reader cannot tell a run still going from one
    /// that ended without saying so.
    /// </para>
    /// </remarks>
    private async Task<ToolResult<BlueprintAuthoringData>> RunFinalizeAsync(
        DraftContext ctx, bool reviewable, CancellationToken cancellationToken)
    {
        string probe = BlueprintProbeNaming.ForSlug(ctx.Slug);
        long startedAt = Stopwatch.GetTimestamp();

        _journal.BlueprintAuthoringStarted(ctx.Game, probe);

        ToolResult<BlueprintAuthoringData> result;
        try
        {
            result = await RunFinalizeCoreAsync(ctx, reviewable, cancellationToken);
        }
        catch
        {
            // A run that threw still ended, and the probe it installed is still what the engine's rows
            // name. Recording the close and rethrowing keeps the span honest.
            _journal.BlueprintAuthored(
                ctx.Game, probe, AuthoringOutcome.Failed, Elapsed(startedAt));
            throw;
        }

        _journal.BlueprintAuthored(
            ctx.Game, probe, Concluded(result.Data?.Outcome), Elapsed(startedAt));

        return result;
    }

    private static long Elapsed(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalSeconds;

    /// <summary>
    /// How the run ended, in the journal's three words.
    /// </summary>
    /// <remarks>
    /// The outcomes that stop before anything is installed — disabled, already in the catalog, not
    /// feasible — never reach here, because the bracket only wraps the half that installs a probe.
    /// Everything that is not a verified blueprint or a draft handed back is a run that did not produce
    /// one, which is what <see cref="AuthoringOutcome.Failed"/> says.
    /// </remarks>
    private static AuthoringOutcome Concluded(BlueprintAuthoringOutcome? outcome) => outcome switch
    {
        BlueprintAuthoringOutcome.Verified => AuthoringOutcome.Verified,
        BlueprintAuthoringOutcome.DraftReady => AuthoringOutcome.DraftReady,
        _ => AuthoringOutcome.Failed,
    };

    private async Task<ToolResult<BlueprintAuthoringData>> RunFinalizeCoreAsync(
        DraftContext ctx, bool reviewable, CancellationToken cancellationToken)
    {
        var game = ctx.Game;
        var slug = ctx.Slug;
        var subject = ctx.Subject;
        var draft = ctx.Draft;
        var provenance = ctx.Provenance;
        var findings = ctx.Findings;

        // --- Steps 5-10: persist → validate → test-install → verify → self-repair → teardown -----
        var probeName = BlueprintProbeNaming.ForSlug(slug);
        var verifyLog = new List<string>();
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        bool verified = false;
        string? proofLine = null;
        string? observedReadyRegex = null; // a ready line the verify step observed in the real boot log,
                                           // to write into the kept blueprint when it carried no regex
        string? lastBootLog = null;        // most recent boot evidence, surfaced on a review-path recovery card

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
            var emptyDownload = false;
            string? installError = null;
            InstanceHealthSnapshot? lastSnapshot = null;
            RepairEvidence? evidence = null;
            try
            {
                var (actor, origin) = Provenance();
                installAttempted = true;
                _progress.Report(ResultCardKinds.BlueprintDraft, "install", "Test-installing a copy to try it out…");
                KgsmResult installResult = await Task.Run(
                    // The probe's name is an ID this code minted and then addresses everything by —
                    // the log read, the file walk, the teardown — so it is passed as the id rather
                    // than as a label the engine would generate an id beside.
                    () => _instances.Install(
                        slug, library: null, version: null, id: probeName,
                        actor: actor, origin: origin, port: null, start: true),
                    cancellationToken);

                installSucceeded = installResult.IsSuccess;
                if (!installSucceeded)
                {
                    installError = installResult.Stderr;
                    verifyLog.Add($"attempt {attempt}: test-install failed ({installResult.Stderr})");
                }
                // A Steam-account-owned title downloads NOTHING under the anonymous login the test-install
                // uses — SteamCMD connects, reports "Update state (0x0)", and kgsm reports success with an
                // EMPTY install dir. That empty dir (measured, not inferred) is the honest signal that the
                // server files aren't anonymously downloadable — surfaced as RequiresSteamAccount below.
                else if (!InstallHasGameFiles(probeName))
                {
                    emptyDownload = true;
                    verifyLog.Add($"attempt {attempt}: install reported success but produced no game files (anonymous download got nothing)");
                }
                else
                {
                    _progress.Report(ResultCardKinds.BlueprintDraft, "verify", "Booting it up and waiting for it to answer…");
                    (verified, proofLine, lastSnapshot, observedReadyRegex) = await VerifyAsync(probeName, draft, cancellationToken);
                    verifyLog.Add(verified
                        ? $"attempt {attempt}: verified — {proofLine}"
                        : $"attempt {attempt}: did not verify booting + listening within the timeout");
                }

                // Gather the ground-truth evidence WHILE the probe's files are still on disk (before the
                // finally block tears them down): the real install tree, the shipped launch scripts, and
                // the boot log. Needed to repair (not verified, attempts remain) — and, on the review path,
                // also on the FINAL failing attempt so the recovery card can show the user the last boot log.
                if (!verified && !emptyDownload && (attempt < maxAttempts || reviewable))
                {
                    evidence = await GatherEvidence(probeName, installSucceeded, installError, lastSnapshot, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(evidence.BootLog))
                        lastBootLog = evidence.BootLog;
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
                    _progress.Report(ResultCardKinds.BlueprintDraft, "teardown", "Cleaning up the test copy…");
                    await TeardownAsync(probeName);
                }
            }

            // An empty anonymous download won't change on retry — surface the measured "needs an owning
            // account" outcome now (the enum's only producer is this empirical check, not a research guess).
            if (emptyDownload)
            {
                _files.Remove(slug);
                _invalidation.Invalidate();
                var reason = $"I installed \"{game}\" but the anonymous download produced no server files — its " +
                              $"server files most likely need a Steam account that owns the game, which the automatic " +
                              $"test-install can't use. You can still add \"{game}\" manually with an owning account.";
                await StashAsync(game, slug, findings, verifyLog, BlueprintAuthoringOutcome.NotFeasible, reason, RenderForStash(draft), cancellationToken);
                return Envelope(subject, reason, BlueprintAuthoringOutcome.NotFeasible, game, null, provenance, null, reason, false);
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
            _progress.Report(ResultCardKinds.BlueprintDraft, "repair", "Reading what actually installed to fix the config…");
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
            // If verification succeeded on a ready line the verify step observed in the real boot log (the
            // draft carried no startup_success_regex), write that observed line into the kept blueprint — a
            // future health check needs the readiness signal, and an observed line is the best-sourced value
            // there is (measured, not researched).
            if (observedReadyRegex is not null)
            {
                draft = draft with { Native = draft.Native with { StartupSuccessRegex = observedReadyRegex } };
                var rewrite = _files.Create(draft, overwrite: true);
                if (rewrite.Outcome == FileOpOutcome.Ok)
                {
                    provenance = AppendObserved(provenance, "startup_success_regex", observedReadyRegex);
                    verifyLog.Add($"wrote observed readiness regex into the blueprint: {observedReadyRegex}");
                }
            }

            _invalidation.Invalidate();
            var summary = $"I didn't have \"{game}\", so I researched it, built a config, and test-ran it — " +
                           $"{proofLine}. **{game} is now in the catalog.** Want me to make you a server?";
            return Envelope(subject, summary, BlueprintAuthoringOutcome.Verified, game, slug, provenance, proofLine, null, true);
        }

        _invalidation.Invalidate();
        await StashAsync(game, slug, findings, verifyLog, BlueprintAuthoringOutcome.Failed,
            "test-install never verified booting and listening within the self-repair bound", RenderForStash(draft), cancellationToken);

        // Review path: hand the last draft back for another edit rather than giving up — the user can close
        // the gap the autonomous repair couldn't (a wrapper it wouldn't switch off, a game-specific flag),
        // with the real boot log in front of them. Nothing is in the catalog (the draft was removed above).
        if (reviewable)
        {
            var recoverReason = "I couldn't get it to boot and answer within a few tries. Here's what the last " +
                                 "boot logged — tweak the config below and save to try again, or cancel to stop.";
            return Envelope(subject, recoverReason, BlueprintAuthoringOutcome.DraftReady, game, slug, provenance,
                null, recoverReason, false, draftYaml: _files.Render(draft), evidence: TrimEvidence(lastBootLog), editable: true);
        }

        var failSummary = $"I researched \"{game}\" and tried to build and test a config for it, but couldn't " +
                           "get it to boot and answer on its port — so I didn't add it to the catalog.";
        return Envelope(subject, failSummary, BlueprintAuthoringOutcome.Failed, game, null, provenance, null, failSummary, false);
    }

    // --- verify: boots + listens ------------------------------------------------------------------

    private const int VerifyLogLines = 400;   // full-log tail pulled each poll for the readiness match

    private async Task<(bool Verified, string? ProofLine, InstanceHealthSnapshot? LastSnapshot, string? ObservedReadyRegex)> VerifyAsync(
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
        var sawRunning = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = await _operations.GetHealthSnapshotAsync(probeName, cancellationToken);
            if (health.IsSuccess)
            {
                var snap = health.Value!;
                last = snap;

                // Match readiness against the FULL boot log, not the status snapshot's 3-line tail — a ready
                // line ("Server started", "Opened Steam server", …) prints during boot and scrolls out of a
                // tiny tail long before the check runs. GetLogsAsync returns the whole captured log.
                var fullLog = await FetchFullLogAsync(probeName, VerifyLogLines, cancellationToken);
                var portsUp = snap.PortsReachable == true;
                var regexMatched = successRegex is not null && successRegex.IsMatch(fullLog);
                // When the draft carries no readiness regex (research/repair didn't source one) and the port
                // isn't detectable (relay/NAT-punch servers never bind a local port), fall back to a curated
                // set of well-known ready lines — matched only while the server is RUNNING, and written back
                // into the kept blueprint so it isn't a fabricated-from-nothing signal.
                var genericPattern = successRegex is null && !portsUp ? MatchGenericReady(fullLog) : null;

                if (snap.Running == true && (portsUp || regexMatched || genericPattern is not null))
                {
                    if (portsUp)
                        return (true, "it booted and is listening on its configured port", snap, null);
                    if (regexMatched)
                        return (true, "it booted and its startup message appeared in the logs", snap, null);
                    return (true, "it booted and a known server-ready line appeared in the logs", snap, genericPattern);
                }

                // Fast-fail on a crash: a server that came up and then exited (a bad argument, a missing
                // dependency) won't recover — stop polling now so the repair cycle starts in seconds instead
                // of waiting out the whole timeout. The full log is captured for repair either way.
                // Only a measured stop after a measured start is a probe that died. A run state that
                // could not be read is not evidence the server exited, so the poll keeps waiting for
                // one that is rather than failing the attempt on an absence.
                if (snap.Running == true)
                    sawRunning = true;
                else if (snap.Running == false && sawRunning)
                    return (false, null, snap, null);
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return (false, null, last, null);
    }

    /// <summary>The full captured boot log as one string, via kgsm-lib's log reader (not the status
    /// snapshot's 3-line tail). Best-effort — a read failure degrades to empty, never throws.</summary>
    private async Task<string> FetchFullLogAsync(string probeName, int maxLines, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await _instances.GetLogsAsync(probeName, maxLines, cancellationToken);
            return lines is null || lines.Count == 0 ? string.Empty : string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Blueprint verify/repair: reading the full boot log failed for {Probe}", probeName);
            return string.Empty;
        }
    }

    // Curated "server is ready" lines from the shipped catalog + common engines — the readiness signal for a
    // server that neither binds a detectable local port nor came with a sourced regex. Substrings, matched
    // case-insensitively against the full log; the matching substring is written back as the blueprint's
    // startup_success_regex (a regex-safe literal), so a KEEP always ends with a real, measured signal.
    private static readonly string[] GenericReadyLines =
    [
        "Opened Steam server", "Server started", "Started server using port", "Game ID",
        "up for play", "SERVER STARTED", "Hosting game", "StartGame done",
        "Dedicated server host started", "For help, type", "Done (",
    ];

    private static string? MatchGenericReady(string fullLog)
    {
        if (string.IsNullOrEmpty(fullLog))
            return null;
        foreach (var line in GenericReadyLines)
        {
            if (fullLog.Contains(line, StringComparison.OrdinalIgnoreCase))
                return System.Text.RegularExpressions.Regex.Escape(line);
        }
        return null;
    }

    private static List<BlueprintFieldProvenance> AppendObserved(
        List<BlueprintFieldProvenance> provenance, string field, string value)
    {
        provenance.Add(new BlueprintFieldProvenance(field, value, "observed:test-install-boot-log"));
        return provenance;
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

    private const int RepairLogLines = 600;   // full-log tail handed to the repair step as boot evidence

    // SteamCMD's own bookkeeping dir — present (holding only an appmanifest) even when an anonymous download
    // of an owned title fetched no actual game files, so it must NOT count as "the game installed".
    private const string SteamMetadataDir = "steamapps";

    /// <summary>True if the install dir holds at least one real game FILE (checked one level deep,
    /// ignoring SteamCMD's <c>steamapps</c> metadata dir). A Steam-account-owned title downloads nothing
    /// under the anonymous test-install login: the install "succeeds" but leaves only
    /// <c>steamapps/appmanifest_&lt;id&gt;.acf</c> and no game files — this is the measured signal for that
    /// case. A listing failure (dir absent) reads as "no files", the same conservative outcome.</summary>
    private bool InstallHasGameFiles(string probeName)
    {
        try
        {
            var root = _instanceFiles.List(probeName, InstallRoot, 100);
            if (!root.IsOk || root.Value is null)
                return false;

            foreach (var entry in root.Value.Entries)
            {
                if (entry.Kind == FileKind.File)
                    return true;
                if (entry.Kind == FileKind.Dir)
                {
                    if (entry.Name.Equals(SteamMetadataDir, StringComparison.OrdinalIgnoreCase))
                        continue; // SteamCMD metadata, not game content
                    var sub = _instanceFiles.List(probeName, $"{InstallRoot}/{entry.Name}", 100);
                    if (sub.IsOk && sub.Value is not null && sub.Value.Entries.Any(e => e.Kind == FileKind.File))
                        return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Blueprint authoring: checking the install dir for game files failed for {Probe}", probeName);
            return false;
        }
    }

    /// <summary>Reads the probe's real install directory and boot log into a <see cref="RepairEvidence"/>.
    /// Best-effort: any read failure degrades to an empty section, never throws (except cancellation).</summary>
    private async Task<RepairEvidence> GatherEvidence(
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

        // The full boot log kgsm captured (the instance's own stdout .log — the whole thing, not the status
        // snapshot's 3-line tail). This is what BOTH the verify check and repair read — but a game that
        // redirects its output to its own file (e.g. Unity's `-logfile`) leaves it empty. So supplement it
        // with any log files the game itself wrote into the install dir: the readiness line ("Game ID",
        // "Server started") is in there even when kgsm's capture is blank, which is what lets repair
        // synthesize a startup_success_regex AND learn to drop the redirect.
        var bootLog = new StringBuilder();
        var capturedStdout = await FetchFullLogAsync(probeName, RepairLogLines, cancellationToken);
        if (!string.IsNullOrWhiteSpace(capturedStdout))
            bootLog.Append("--- monitored stdout (what the readiness check sees) ---\n")
                   .Append(capturedStdout).Append('\n');

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
        IReadOnlyList<BlueprintFieldProvenance> provenance, string? proofLine, string? reason, bool offerInstance,
        string? draftYaml = null, string? evidence = null, bool editable = false) =>
        new(
            ResultCardKinds.BlueprintDraft,
            outcome == BlueprintAuthoringOutcome.Verified ? Confidence.Confirmed : Confidence.Likely,
            subject,
            summary,
            new BlueprintAuthoringData(outcome, game, blueprintName, provenance, proofLine, reason, offerInstance,
                draftYaml, evidence, editable));

    // --- review-path helpers ------------------------------------------------------------------------

    // A $VAR / ${VAR} reference. Only $instance_* is a real kgsm runtime placeholder; anything else expands
    // to empty at launch, so a user edit that reintroduces one must go back to the editor, not to install.
    private static readonly System.Text.RegularExpressions.Regex PlaceholderToken =
        new(@"\$\{?(\w+)\}?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True if <paramref name="args"/> references any <c>$TOKEN</c> that isn't a real
    /// <c>$instance_*</c> variable — the same guard the synthesizer applies, enforced again on a user edit
    /// (the finalize entry can't trust hand-typed args).</summary>
    private static bool HasForeignPlaceholder(string? args)
    {
        if (string.IsNullOrEmpty(args))
            return false;
        foreach (System.Text.RegularExpressions.Match m in PlaceholderToken.Matches(args))
        {
            if (!m.Groups[1].Value.StartsWith("instance_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Provenance for a user-reviewed draft: the launch-critical fields tagged as user-authored
    /// (there's no web source once a human has edited the YAML). Admin-facing, like the research provenance.</summary>
    private static List<BlueprintFieldProvenance> ProvenanceFromDraft(NativeBlueprintDraft draft)
    {
        const string src = "user-reviewed";
        string? OrNull(string v) => string.IsNullOrEmpty(v) ? null : v;
        return
        [
            new("executable_file", OrNull(draft.Native.ExecutableFile), src),
            new("executable_subdirectory", OrNull(draft.Native.ExecutableSubdirectory), src),
            new("executable_arguments", OrNull(draft.Native.ExecutableArguments), src),
            new("ports", OrNull(draft.Native.Ports), src),
            new("startup_success_regex", OrNull(draft.Native.StartupSuccessRegex), src),
        ];
    }

    /// <summary>Placeholder findings for a finalize started from a user-edited draft — there is no research
    /// run behind it, so the stash record simply carries an empty, Feasible finding set.</summary>
    private static BlueprintResearchFindings EmptyFindings(string game) =>
        new(BlueprintFeasibility.Feasible, game, [], [], "user-reviewed draft");

    private const int MaxEvidenceChars = 2000; // boot-log excerpt shown on the recovery card

    /// <summary>Trims the boot log to a tail excerpt for the recovery card (the ready/error lines are at the
    /// end); null/blank stays null so the card simply omits the evidence block.</summary>
    private static string? TrimEvidence(string? bootLog)
    {
        if (string.IsNullOrWhiteSpace(bootLog))
            return null;
        var trimmed = bootLog.TrimEnd();
        return trimmed.Length <= MaxEvidenceChars ? trimmed : "…\n" + trimmed[^MaxEvidenceChars..];
    }
}
