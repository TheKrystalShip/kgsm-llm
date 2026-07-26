using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Fetch;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.RootCause;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Maps model tool calls onto the host's server ports. The set of cases here,
/// together with <see cref="LlmTools"/>, forms the security boundary: a tool name
/// not matched below is refused.
///
/// Inventory reads (listing/resolving instances and blueprints) go through the
/// cached <see cref="IServerInventory"/>; live status reads go through
/// <see cref="IServerOperations"/>. Every COMMAND — the merged lifecycle
/// <c>server_command</c> (start/stop/restart/update/backup), plus install/uninstall and
/// set_config — is propose-only (§3.5): the dispatcher resolves and STAGES it into the
/// <see cref="IConfirmationContext"/> — it is never executed here. The matching op runs
/// later, from <see cref="ServerAssistant.ConfirmAsync"/>, only after a human confirms.
/// <para>
/// The ONE exception is an auto-accept turn (<see cref="IConfirmationContext.AutoExecute"/>, set by
/// the host after the api verified admin-tier ∧ toggle): there the <c>server_command</c> lifecycle
/// verbs run immediately here (see <c>ExecuteCommandNowAsync</c>) instead of staging. Install /
/// uninstall / set-config are NOT auto-executed even then — they keep their own stage methods.
/// </para>
/// </summary>
public class ToolDispatcher : IToolDispatcher
{
    private readonly IServerOperations _operations;
    private readonly IServerInventory _inventory;
    private readonly IConfirmationContext _confirmations;
    private readonly ISearch _search;
    private readonly IWebFetch _webFetch;
    private readonly IServerMetrics _metrics;
    private readonly IEventHistory _events;
    private readonly INetworkInfo _network;
    private readonly IUpnpInfo _upnp;
    private readonly IBlueprintAuthoring _blueprintAuthoring;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IServerOperations operations,
        IServerInventory inventory,
        IConfirmationContext confirmations,
        ISearch search,
        IWebFetch webFetch,
        IServerMetrics metrics,
        IEventHistory events,
        INetworkInfo network,
        IUpnpInfo upnp,
        IBlueprintAuthoring blueprintAuthoring,
        ILogger<ToolDispatcher> logger)
    {
        _operations = operations;
        _inventory = inventory;
        _confirmations = confirmations;
        _search = search;
        _webFetch = webFetch;
        _metrics = metrics;
        _events = events;
        _network = network;
        _upnp = upnp;
        _blueprintAuthoring = blueprintAuthoring;
        _logger = logger;
    }

    public async Task<ToolOutput> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Dispatching tool '{Tool}' args={Args}",
            call.Name, string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            if (call.Name == LlmTools.GetStatus)
                return await GetStatusAsync(call, cancellationToken);
            if (call.Name == LlmTools.ListBlueprints)
                return await ListBlueprintsAsync(cancellationToken);
            if (call.Name == LlmTools.RunHealthCheck)
                return await RunHealthCheckAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetPerformance)
                return await GetPerformanceAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetNetwork)
                return await GetNetworkAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetAuditLog)
                return await GetAuditLogAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetChangeTimeline)
                return await GetChangeTimelineAsync(call, cancellationToken);
            if (call.Name == LlmTools.TraceRootCause)
                return await TraceRootCauseAsync(call, cancellationToken);
            if (call.Name == LlmTools.Search)
                return await SearchAsync(call, cancellationToken);
            if (call.Name == LlmTools.FetchUrl)
                return await FetchUrlAsync(call, cancellationToken);
            if (call.Name == LlmTools.CreateBlueprint)
                return await CreateBlueprintAsync(call, cancellationToken);
            if (call.Name == LlmTools.ReviseBlueprint)
                return await ReviseBlueprintAsync(call, cancellationToken);
            if (call.Name == LlmTools.ReadFile)
                return await ReadFileAsync(call, cancellationToken);
            if (call.Name == LlmTools.ListFiles)
                return await ListFilesAsync(call, cancellationToken);
            if (call.Name == LlmTools.ServerCommand)
                return await StageServerCommandAsync(call, cancellationToken);
            if (call.Name == LlmTools.UninstallServer)
                return await StageUninstallAsync(call, cancellationToken);
            if (call.Name == LlmTools.InstallServer)
                return await StageInstallAsync(call, cancellationToken);
            if (call.Name == LlmTools.SetConfigValue)
                return await StageSetConfigAsync(call, cancellationToken);
            if (call.Name == LlmTools.OpenPorts)
                return await StageOpenPortsAsync(call, cancellationToken);
            if (call.Name == LlmTools.WriteFile)
                return await StageWriteFileAsync(call, cancellationToken);

            return $"Error: '{call.Name}' is not a known tool.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{Tool}' threw", call.Name);
            return $"Error: the '{call.Name}' tool failed unexpectedly.";
        }
    }

    private async Task<string> ListBlueprintsAsync(CancellationToken cancellationToken)
    {
        var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
        if (blueprints.Count == 0)
            return "There are no installable blueprints.";

        var names = blueprints.OrderBy(k => k);
        return "Installable game types:\n" + string.Join("\n", names.Select(n => $"- {n}"));
    }

    /// <summary>
    /// The unified knowledge lookup via the <see cref="ISearch"/> aggregator (plan §3.4): local
    /// indexed docs first, public web fallback. The aggregator returns ready-to-use grounding text
    /// (and honest "nothing found" / "couldn't search" messages) and never throws, so this handler
    /// only guards the blank query and relays. The per-message call cap is enforced upstream in the
    /// assistant gate; the per-day web wallet cap lives host-side in the web provider.
    /// </summary>
    private async Task<ToolOutput> SearchAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var query = call.Arg("query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return "Error: search needs a 'query'.";

        // The aggregator returns the model's grounding text (Summary) plus the cited passages (Data).
        // Attach the surface card only when there is something to cite — an empty / "couldn't search"
        // outcome stays summary-only, exactly like a summary-only lifecycle read (mirrors run_health_check).
        var result = await _search.SearchAsync(query, cancellationToken);
        return result.Data.Passages.Count > 0
            ? new ToolOutput(result.Summary, ToolResultCard.From(result))
            : result.Summary;
    }

    /// <summary>Caps the text carried in the model-facing <see cref="ToolOutput.Summary"/> for
    /// <c>fetch_url</c> — protects the small model's context window even when the adapter's own
    /// (much larger, byte-based) size cap wasn't hit. Independent of the adapter's <c>Truncated</c>
    /// flag; either one clipping the content sets the card/summary's truncated note.</summary>
    private const int FetchSummaryCharCap = 6000;

    /// <summary>
    /// Reads ONE specific page via <see cref="IWebFetch"/> — the model already has (or just found) a
    /// URL and wants its actual content, unlike <c>search</c> which only returns provider-summarized
    /// hits. The port handles all safety (scheme allowlist, an SSRF guard re-validated on every
    /// redirect hop, a size cap, a timeout, content-type filtering) and never throws; a blocked or
    /// failed fetch surfaces here as an honest "couldn't fetch" message — never as "the page is
    /// empty" (the same honesty rule <see cref="SearchAsync"/> follows for web failures). A
    /// successful fetch always carries a <see cref="FetchData"/> card (real fetched structure, unlike
    /// an empty search that has nothing to cite); a failure stays summary-only.
    /// </summary>
    private async Task<ToolOutput> FetchUrlAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var url = call.Arg("url")?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return "Error: fetch_url needs a 'url'.";

        var result = await _webFetch.FetchAsync(url, cancellationToken);
        if (!result.IsSuccess)
            return $"Couldn't fetch {url}: {result.Error ?? "unknown error"}.";

        var page = result.Value!;
        var text = page.Text.Trim();
        var truncated = page.Truncated;
        if (text.Length > FetchSummaryCharCap)
        {
            text = text[..FetchSummaryCharCap];
            truncated = true;
        }

        var truncNote = truncated ? " (truncated)" : "";
        var summary = text.Length == 0
            ? $"Fetched {page.FinalUrl} ({page.ContentType ?? "unknown content type"}), but it had no readable text."
            : $"Fetched {page.FinalUrl}{truncNote}:\n{text}";

        var card = new FetchData(page.FinalUrl, page.Title, text, truncated);
        var envelope = new ToolResult<FetchData>(
            LlmTools.FetchUrl, Confidence.Confirmed, new ResultRef(ResourceKind.WebPage, page.FinalUrl), summary, card);
        return new ToolOutput(summary, ToolResultCard.From(envelope));
    }

    /// <summary>
    /// The <c>create_blueprint</c> authoring pipeline (plan §"Pipeline"), run entirely by
    /// <see cref="IBlueprintAuthoring"/> — this handler only validates the argument and relays. Unlike
    /// every command tool, this is NOT staged: it is authorized-and-autonomous (§LlmTools.AuthorizedActions),
    /// so it runs to completion here and returns the real outcome. Always carries a card (even a
    /// "couldn't do this one" outcome is worth showing — mirrors <see cref="GetAuditLogAsync"/>).
    /// </summary>
    private async Task<ToolOutput> CreateBlueprintAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var game = call.Arg("game")?.Trim();
        if (string.IsNullOrWhiteSpace(game))
            return "Error: create_blueprint needs a 'game'.";

        // Mandatory-review flow: DRAFT only (research + build) — the test-install/verify runs later, when a
        // permitted human saves the config. A ready draft is staged as a Blueprint confirmation carrying the
        // draft YAML the user edits; every terminal outcome (disabled, already-exists, infeasible) just
        // returns its card, staging nothing.
        var result = await _blueprintAuthoring.DraftAsync(game, cancellationToken);
        var data = result.Data;
        if (data is not null && data.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
        {
            _confirmations.Stage(new PendingConfirmation(
                ConfirmationKind.Blueprint, data.BlueprintName ?? game,
                InstanceName: game, ConfigValue: data.DraftYaml));

            return new ToolOutput(
                "Drafted a starting config and shown it to the user in an editor to review and edit. NOTHING is " +
                "installed yet — the test-install runs only when they save it. Tell them to review/tweak the " +
                "config and save it to have you test-run and verify it; do NOT claim the game is added yet.",
                ToolResultCard.From(result));
        }

        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    private async Task<ToolOutput> ReviseBlueprintAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var revisedYaml = call.Arg("revised_yaml");
        if (string.IsNullOrWhiteSpace(revisedYaml))
            return "Error: revise_blueprint needs the complete updated YAML in 'revised_yaml'.";

        // Same staging as create_blueprint: a successful revision is a fresh editable draft — re-stage a
        // Blueprint confirmation carrying the new YAML (superseding the prior draft's token) and hand back
        // the updated card. The user's Save still runs the test-install/verify (FinalizeAsync).
        var result = await _blueprintAuthoring.ReviseAsync(revisedYaml, cancellationToken);
        var data = result.Data;
        if (data is not null && data.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
        {
            _confirmations.Stage(new PendingConfirmation(
                ConfirmationKind.Blueprint, data.BlueprintName ?? data.Game,
                InstanceName: data.Game, ConfigValue: data.DraftYaml));

            return new ToolOutput(
                "Applied the change and re-showed the updated draft in the editor. NOTHING is installed yet — " +
                "the test-install runs only when they save it. Tell them what you changed and to review/save; " +
                "do NOT claim the game is added yet.",
                ToolResultCard.From(result));
        }

        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// Reads a text file from inside the resolved instance's own directory (its config,
    /// logs, server.properties, mod settings, …). The <c>path</c> arg is relative to that
    /// directory; an omitted/blank path defaults to the instance's main
    /// <c>&lt;name&gt;.config.ini</c> (preserving the old view_config_file affordance — the
    /// common "show me X's config" ask stays a single, path-free call). The port enforces
    /// the instance-directory jail (<c>..</c>/out-of-tree-symlink refusal), refuses
    /// non-regular files (a FIFO would otherwise block), caps size, and skips binaries.
    /// Content is returned verbatim — no redaction (owner decision: game-server files,
    /// trusted operators).
    /// </summary>
    private async Task<string> ReadFileAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var path = call.Arg("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = $"{resolved}.config.ini";

        var result = await _operations.ReadInstanceFileAsync(resolved!, path, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not read '{path}' for '{resolved}' ({result.Error ?? "unknown error"}).";

        return $"File ({path}) for {resolved}:\n{result.Value ?? string.Empty}";
    }

    /// <summary>
    /// Lists one level of the resolved instance's own directory so the model can discover a
    /// file to read with <c>read_file</c>. An omitted/blank <c>subdir</c> lists the top level;
    /// otherwise it lists that subdirectory. Same instance-directory jail as the read path.
    /// </summary>
    private async Task<string> ListFilesAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var subdir = call.Arg("subdir")?.Trim();
        var hasSubdir = !string.IsNullOrWhiteSpace(subdir);

        var result = await _operations.ListInstanceDirectoryAsync(
            resolved!, hasSubdir ? subdir : null, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not list files for '{resolved}' ({result.Error ?? "unknown error"}).";

        var where = hasSubdir ? $"{resolved}/{subdir!.Trim('/')}" : resolved;
        var entries = result.Value!;
        if (entries.Count == 0)
            return $"{where} is empty.";

        var lines = entries.Select(e =>
            e.IsDirectory ? $"- {e.Name}/" : $"- {e.Name} ({FormatSize(e.Size)})");
        return $"Files in {where}:\n{string.Join("\n", lines)}";
    }

    /// <summary>Compact human size for a directory listing (B / KB / MB).</summary>
    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} B";

    /// <summary>
    /// Merged status read (toolbox catalog §4.1): no instance_name → a single
    /// fleet-wide summary (the one-shot replacement for fanning a per-instance
    /// liveness loop, which is the agent-loop iteration-cap cause); an
    /// instance_name → detailed status for that one server.
    /// <para>
    /// Only the fleet mode carries a structured card (Phase 2 §5·b): it has structured data
    /// (<see cref="FleetStatusEntry"/>[]). The single-server mode returns kgsm's opaque status
    /// string — no structured source — so it stays summary-only (a card would be fabricated).
    /// </para>
    /// </summary>
    private async Task<ToolOutput> GetStatusAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var name = call.Arg("instance_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return await GetFleetStatusAsync(cancellationToken);

        var (resolved, error) = await ResolveInstanceAsync(name, cancellationToken);
        if (error is not null)
            return error;

        var result = await _operations.GetStatusAsync(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not get status for '{resolved}' ({result.Error ?? "unknown error"}).";

        return $"Status for {resolved}:\n{result.Value}";
    }

    /// <summary>
    /// One-shot fleet status (Phase 2 §5·b): fetches the neutral entries and returns a
    /// <see cref="ToolOutput"/> whose <c>Summary</c> is the model's grounding text AND whose
    /// <c>Data</c> carries the <see cref="FleetStatusCard"/>. An instance whose status could not be
    /// read surfaces as "status unavailable (reason)"/<see cref="ServerRunState.Unknown"/>, never
    /// collapsed to "stopped" — the model (and the card) must not narrate a read failure as a
    /// measured state. A read FAILURE is summary-only (no card); an empty fleet is a real measured
    /// result → an empty card. The projection + summary live in the pure <see cref="FleetStatusCard"/>.
    /// </summary>
    private async Task<ToolOutput> GetFleetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _operations.GetFleetStatusAsync(cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not read server status ({result.Error ?? "unknown error"}).";

        var card = FleetStatusCard.Build(result.Value!);
        return new ToolOutput(card.Summary, ToolResultCard.From(card));
    }

    /// <summary>
    /// The first aggregator (toolbox-plan §3.4): resolves the instance, fetches the
    /// neutral health inputs via the port, and runs the deterministic
    /// <see cref="HealthCheckAggregator"/>. Returns a <see cref="ToolOutput"/> whose
    /// <c>Summary</c> is the model's grounding text (§3.6) AND whose <c>Data</c> carries the
    /// structured <see cref="ToolResultCard"/> (toolbox-plan §5·c) for a streaming surface — the
    /// only tool that has a real card today (Phase 2). The model still sees only the Summary; the
    /// card never re-enters the conversation. All judgment lives in the aggregator, so this
    /// handler only orchestrates. The error paths return a bare string (implicitly a summary-only
    /// <see cref="ToolOutput"/>) — no card.
    /// </summary>
    private async Task<ToolOutput> RunHealthCheckAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var result = await _operations.GetHealthSnapshotAsync(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not run a health check on '{resolved}' ({result.Error ?? "unknown error"}).";

        var health = HealthCheckAggregator.Run(result.Value!, resolved!);
        return new ToolOutput(health.Summary, ToolResultCard.From(health));
    }

    /// <summary>
    /// Live per-server resource usage (mirrors <see cref="RunHealthCheckAsync"/>): resolves the
    /// instance, reads the neutral metrics via <see cref="IServerMetrics"/> (a snapshot from the
    /// kgsm-monitor's latest frame), and runs the pure <see cref="PerformanceReport"/>. Only a
    /// <see cref="PerformanceState.Live"/> read carries a card (measured values to render); a
    /// not-running or monitor-unavailable read stays summary-only — carding it would fabricate
    /// structure we don't have. The port never throws, so there is no error path here; a failed
    /// read arrives as <see cref="PerformanceState.MonitorUnavailable"/>, which the aggregator words
    /// honestly.
    /// </summary>
    private async Task<ToolOutput> GetPerformanceAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        // A `range` argument switches from the point-in-time snapshot to a windowed TREND read (a chart
        // of how usage changed) — pulled on demand from the monitor's history, the single source of truth.
        string? range = call.Arg("range");
        if (!string.IsNullOrWhiteSpace(range))
        {
            var history = await _metrics.GetHistoryAsync(resolved!, range.Trim(), cancellationToken);
            var trend = PerformanceReport.BuildHistory(history, resolved!);
            // Card only when there's a series to plot; empty/unavailable stays summary-only (no fabricated chart).
            return trend.Data.Series is { Count: > 0 } s && s.Values.Any(p => p.Count > 0)
                ? new ToolOutput(trend.Summary, ToolResultCard.From(trend))
                : trend.Summary;
        }

        var reading = await _metrics.GetSnapshotAsync(resolved!, cancellationToken);
        var report = PerformanceReport.Build(reading, resolved!);
        return report.Data.State == PerformanceState.Live
            ? new ToolOutput(report.Summary, ToolResultCard.From(report))
            : report.Summary;   // NotRunning / MonitorUnavailable stay summary-only (no card)
    }

    /// <summary>
    /// The network read (mirrors <see cref="GetPerformanceAsync"/>): resolves the instance, reads the two
    /// independent authorities — the host firewall via <see cref="INetworkInfo"/> (kgsm-firewall) and the
    /// router / UPnP forwards via <see cref="IUpnpInfo"/> (the watchdog) — and runs the pure
    /// <see cref="NetworkReport"/>. A card is attached when EITHER axis has real measured structure (the
    /// firewall is <see cref="NetworkState.Available"/> OR the router was <see cref="UpnpState.Queried"/>);
    /// when both are unavailable it stays summary-only rather than card an empty shell. Neither port throws,
    /// so there is no error path — each unreachable authority arrives as its honest unavailable state, which
    /// the aggregator words as such (never a fabricated "nothing open" / "nothing forwarded").
    /// </summary>
    private async Task<ToolOutput> GetNetworkAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        // Both authorities read concurrently — independent sockets, neither throws.
        var firewallTask = _network.GetPortsAsync(resolved!, cancellationToken);
        var upnpTask = _upnp.GetForwardsAsync(resolved!, cancellationToken);
        var firewall = await firewallTask;
        var upnp = await upnpTask;

        var report = NetworkReport.Build(firewall, upnp, resolved!);
        bool hasStructure = report.Data.State == NetworkState.Available
                            || report.Data.UpnpState == UpnpState.Queried;
        return hasStructure
            ? new ToolOutput(report.Summary, ToolResultCard.From(report))
            : report.Summary;   // both authorities unavailable → summary-only (no card)
    }

    /// <summary>
    /// The unfiltered engine-event read (toolbox-plan §4.1): resolves an OPTIONAL instance (blank →
    /// every server on this host), maps the model's <c>window</c> to a <c>since</c> bound, reads the
    /// monitor's raw event log via <see cref="IEventHistory"/> directly (never via kgsm-api — plan §9),
    /// and runs the pure <see cref="AuditReport"/>. Always carries a card: an empty/unavailable result
    /// is still a real, honestly-worded answer worth showing, unlike a not-running metrics snapshot.
    /// </summary>
    private async Task<ToolOutput> GetAuditLogAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOptionalInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var (window, sinceMs) = AuditWindow.Resolve(call.Arg("window"), AuditWindow.DefaultAuditWindow, DateTimeOffset.UtcNow);
        var reading = await _events.GetEventsAsync(resolved, sinceMs, EventFetchLimit, cancellationToken);
        var result = AuditReport.Build(reading, resolved, window);
        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// Same source as <see cref="GetAuditLogAsync"/>, narrowed to the state-changing subset and framed
    /// as "what changed" (see <see cref="AuditReport.ChangeEventTypes"/> for the exact set and why).
    /// </summary>
    private async Task<ToolOutput> GetChangeTimelineAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOptionalInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var (range, sinceMs) = AuditWindow.Resolve(call.Arg("range"), AuditWindow.DefaultChangeRange, DateTimeOffset.UtcNow);
        var reading = await _events.GetEventsAsync(resolved, sinceMs, EventFetchLimit, cancellationToken);
        var result = AuditReport.BuildChangeTimeline(reading, resolved, range);
        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// The capstone aggregator (toolbox-plan §3.4/§7·Q1): resolves the REQUIRED instance (root cause
    /// is always about one server), then fetches the three sources <see cref="RootCauseAggregator"/>
    /// composes — the event timeline (<see cref="IEventHistory"/>), a metrics window
    /// (<see cref="IServerMetrics"/>), and the health snapshot (<see cref="IServerOperations.GetHealthSnapshotAsync"/>,
    /// the same neutral input <see cref="RunHealthCheckAsync"/> uses) — IN PARALLEL, so a slow source
    /// doesn't serialize the read. Each source degrades independently and honestly: a failed health
    /// snapshot passes <see langword="null"/> + its error into the aggregator rather than refusing the
    /// whole call; the aggregator's rules table then simply can't evaluate the rules that need it. No
    /// nested model call happens anywhere in this path — <see cref="RootCauseAggregator.Run"/> is pure.
    /// Always attaches a card: even a "nothing matched" correlation or a fully-degraded read is a real,
    /// honestly-worded answer worth showing (mirrors <see cref="GetAuditLogAsync"/>).
    /// </summary>
    private async Task<ToolOutput> TraceRootCauseAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var (range, sinceMs) = AuditWindow.Resolve(call.Arg("range"), AuditWindow.DefaultAuditWindow, DateTimeOffset.UtcNow);

        var eventsTask = _events.GetEventsAsync(resolved, sinceMs, EventFetchLimit, cancellationToken);
        var metricsTask = _metrics.GetHistoryAsync(resolved!, range, cancellationToken);
        var healthTask = _operations.GetHealthSnapshotAsync(resolved!, cancellationToken);
        await Task.WhenAll(eventsTask, metricsTask, healthTask);

        var healthResult = healthTask.Result;
        var result = RootCauseAggregator.Run(
            resolved!, range, eventsTask.Result, metricsTask.Result,
            health: healthResult.IsSuccess ? healthResult.Value : null,
            healthUnavailableReason: healthResult.IsSuccess ? null : healthResult.Error);

        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// The merged lifecycle command (§4.1): maps the model-supplied <c>verb</c>
    /// (start/stop/restart/update/backup) onto its <see cref="ConfirmationKind"/> and
    /// stages it. An unknown or missing verb is refused before anything is staged and the
    /// valid verbs are listed back so the model can self-correct — defense-in-depth behind
    /// the schema <c>enum</c> (a non-enum-aware client could still send a bad verb).
    /// </summary>
    private async Task<string> StageServerCommandAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var verb = call.Arg("verb");
        var kind = LlmTools.ServerCommandKind(verb);
        if (kind is null)
            return $"Error: '{verb ?? "(none)"}' is not a valid server action. " +
                   $"Valid actions: {string.Join(", ", LlmTools.ServerCommandVerbs)}.";

        return await StageCommandAsync(call, kind.Value, cancellationToken);
    }

    /// <summary>
    /// The lifecycle command (§4.1). Default is propose-only (§3.5): resolves the instance, then
    /// STAGES the command for human confirmation instead of executing it — the same path
    /// uninstall/install take. Resolution problems (ambiguous / unknown) short-circuit to the model
    /// so it asks the user, and nothing is staged for an unresolved target.
    /// <para>
    /// EXCEPTION — auto-accept (<see cref="IConfirmationContext.AutoExecute"/>): the api verified the
    /// caller is an admin who turned the toggle on, so the lifecycle verbs (only — install /
    /// uninstall / set-config keep their own stage methods) RUN here and now, and the result string
    /// reports the real outcome so the model narrates it as done. The propose path is unchanged.
    /// </para>
    /// </summary>
    private async Task<string> StageCommandAsync(
        LlmToolCall call, ConfirmationKind kind, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        if (_confirmations.AutoExecute)
            return await ExecuteCommandNowAsync(kind, resolved!, cancellationToken);

        _confirmations.Stage(new PendingConfirmation(kind, resolved!));

        return $"Staged a {ConfirmationKinds.Verb(kind)} of '{resolved}' for confirmation. A confirmation " +
               "prompt with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    /// <summary>
    /// Auto-accept path: runs a resolved lifecycle command immediately via the matching
    /// single-instance <see cref="IServerOperations"/> op and returns a result string for the model.
    /// Mirrors <see cref="ServerAssistant.ConfirmAsync"/>'s execute step (the post-confirm path) — the
    /// authority decision was already made upstream (api admin-tier ∧ toggle → AutoExecute), so there
    /// is no second gate here; the model's tool result IS the outcome.
    /// </summary>
    private async Task<string> ExecuteCommandNowAsync(
        ConfirmationKind kind, string instance, CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, Task<Result>>? op = kind switch
        {
            ConfirmationKind.Start => _operations.StartAsync,
            ConfirmationKind.Stop => _operations.StopAsync,
            ConfirmationKind.Restart => _operations.RestartAsync,
            ConfirmationKind.Update => _operations.UpdateAsync,
            ConfirmationKind.Backup => _operations.CreateBackupAsync,
            _ => null,   // not a lifecycle verb → fall back to staging (defense in depth; server_command never maps here)
        };

        if (op is null)
        {
            _confirmations.Stage(new PendingConfirmation(kind, instance));
            return $"Staged a {ConfirmationKinds.Verb(kind)} of '{instance}' for confirmation — tell the user it's awaiting their confirmation.";
        }

        _logger.LogInformation("Auto-executing {Verb} of {Instance}", ConfirmationKinds.Verb(kind), instance);

        var result = await op(instance, cancellationToken);
        return result.IsSuccess
            ? $"Done — '{instance}' has been {ConfirmationKinds.PastTense(kind)}."
            : $"Could not {ConfirmationKinds.Verb(kind)} '{instance}': {result.Error ?? "unknown error"}.";
    }

    /// <summary>
    /// Destructive: resolves the instance, then STAGES an uninstall for human
    /// confirmation instead of executing it. Resolution problems (ambiguous /
    /// unknown) short-circuit to the model so it asks the user — no confirmation
    /// prompt is shown for an unresolved target.
    /// </summary>
    private async Task<string> StageUninstallAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        _confirmations.Stage(new PendingConfirmation(ConfirmationKind.Uninstall, resolved!));

        return $"Staged an uninstall of '{resolved}' for confirmation. A confirmation prompt " +
               "with a button has been shown to the user. This is NOT done yet and will only " +
               "run if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    /// <summary>
    /// Destructive: resolves the blueprint (and validates any custom instance
    /// name doesn't collide), then STAGES an install for human confirmation.
    /// </summary>
    private async Task<string> StageInstallAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (blueprint, error) = await ResolveBlueprintAsync(call.Arg("blueprint_name"), cancellationToken);
        if (error is not null)
            return error;

        var instanceName = call.Arg("instance_name")?.Trim();
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            if (instances.Keys.Any(k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase)))
                return $"Error: an instance named '{instanceName}' already exists. Ask the user for a different name.";
        }
        else
        {
            instanceName = null;
        }

        _confirmations.Stage(new PendingConfirmation(ConfirmationKind.Install, blueprint!, instanceName));

        var named = instanceName is null ? "" : $" named '{instanceName}'";
        return $"Staged an install of a new '{blueprint}' server{named} for confirmation. A confirmation " +
               "prompt with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    /// <summary>
    /// Propose-only (§3.8): resolves the instance and validates a non-empty key, then
    /// STAGES a set-config for human confirmation — it is never written here. kgsm owns
    /// the key-safety policy (denylist); this stage does not pre-judge the key, so a
    /// refusal surfaces only at confirm time. An empty value is allowed (clears the
    /// setting); a missing/blank key short-circuits to the model.
    /// </summary>
    private async Task<string> StageSetConfigAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var key = call.Arg("config_key")?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return "Error: no config_key was provided.";

        // The value is intentionally NOT trimmed and may be the empty string (clearing
        // the setting). A model that omits it entirely is treated as clearing it.
        var value = call.Arg("config_value") ?? string.Empty;

        _confirmations.Stage(new PendingConfirmation(
            ConfirmationKind.SetConfig, resolved!, InstanceName: null, ConfigKey: key, ConfigValue: value));

        return $"Staged setting '{key}' on '{resolved}' for confirmation. A confirmation prompt " +
               "with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    /// <summary>
    /// Propose-only (§3.5): resolves the instance and parses/validates the port spec, then STAGES an
    /// open-ports for human confirmation — nothing is touched here. The validated ports ride the
    /// confirmation as a canonical string on <c>ConfigValue</c>, and the optional router leg rides on
    /// <c>ConfigKey</c> (<c>"router"</c> ⇒ also open the UPnP forward; null ⇒ host firewall only) — both
    /// round-trip through the existing token with no new payload field. The confirm path re-parses exactly
    /// what was staged. A malformed or out-of-range spec short-circuits to the model, and nothing is staged.
    /// </summary>
    private async Task<string> StageOpenPortsAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        if (!PortSpecParser.TryParse(call.Arg("ports"), out var ports, out var parseError))
            return $"Error: {parseError}";

        var canonical = PortSpecParser.ToCanonical(ports);
        // Arguments arrive as strings; treat true/1/yes (any case) as the opt-in, everything else as off.
        var routerArg = call.Arg("include_router")?.Trim();
        bool includeRouter = routerArg is not null
            && (routerArg.Equals("true", StringComparison.OrdinalIgnoreCase)
                || routerArg.Equals("1", StringComparison.Ordinal)
                || routerArg.Equals("yes", StringComparison.OrdinalIgnoreCase));
        string? scope = includeRouter ? "router" : null;

        _confirmations.Stage(new PendingConfirmation(
            ConfirmationKind.OpenPorts, resolved!, InstanceName: null, ConfigKey: scope, ConfigValue: canonical));

        string scopeText = includeRouter
            ? "host-firewall AND router/UPnP forward for port(s) "
            : "host-firewall port(s) ";
        string routerNote = includeRouter
            ? " The router leg only takes effect if the server has port-forwarding enabled; otherwise it's skipped."
            : " Note this opens the HOST firewall only, not router/UPnP port forwarding.";

        return $"Staged opening {scopeText}{PortSpecParser.ToDisplay(ports)} on '{resolved}' for " +
               "confirmation. A confirmation prompt with a button has been shown to the user. This is NOT done " +
               "yet and will only run if a permitted human clicks Confirm — tell the user it's awaiting their " +
               $"confirmation.{routerNote}";
    }

    /// <summary>Mirrors the <see cref="IServerOperations.WriteInstanceFileAsync"/> adapter's write cap —
    /// enforced here too so an oversized body is refused before it ever reaches a confirmation token or
    /// the Service's pending-write store.</summary>
    private const int MaxWriteBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Propose-only: resolves the instance and validates a non-blank path/content, then STAGES a
    /// whole-file overwrite for human confirmation — nothing is written here. The size cap is
    /// enforced at stage time (not just in <see cref="IServerOperations.WriteInstanceFileAsync"/>) so
    /// an oversized body is refused before it ever reaches a confirmation token or the Service's
    /// pending-write store. ALWAYS stages, even on an auto-accept turn (like set_config/install) — a
    /// whole-file overwrite is too consequential to run without a human looking at the diff.
    /// </summary>
    private async Task<string> StageWriteFileAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var path = call.Arg("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return "Error: no path was provided.";

        // Content is NOT trimmed (leading/trailing whitespace can be meaningful in a config file),
        // but it must be present — an empty write is almost certainly a model mistake, not intent.
        var content = call.Arg("content");
        if (string.IsNullOrEmpty(content))
            return "Error: no content was provided.";

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);
        if (byteCount > MaxWriteBytes)
            return $"Error: the content is {byteCount:N0} bytes, over the {MaxWriteBytes / (1024 * 1024)} MB limit.";

        _confirmations.Stage(new PendingConfirmation(
            ConfirmationKind.WriteFile, resolved!, InstanceName: null, ConfigKey: path, ConfigValue: content));

        return $"Staged writing '{path}' on '{resolved}' for confirmation. A confirmation prompt with a " +
               "preview has been shown to the user. This is NOT done yet and will only run if a permitted " +
               "human confirms it — tell the user it's awaiting their confirmation, and that a running " +
               "server picks up the change on its next restart.";
    }

    /// <summary>
    /// Resolves a model-supplied blueprint name against the live blueprint list:
    /// exact (case-insensitive) wins, else single substring match; ambiguous or
    /// unknown returns a message so the model asks the user / self-corrects.
    /// </summary>
    private async Task<(string? resolved, string? error)> ResolveBlueprintAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, "Error: no blueprint_name was provided.");

        var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
        if (blueprints.Count == 0)
            return (null, "Error: there are no installable blueprints available.");

        var query = name.Trim();

        var exact = blueprints
            .FirstOrDefault(k => string.Equals(k, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return (exact, null);

        var candidates = blueprints
            .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        if (candidates.Count == 1)
            return (candidates[0], null);

        if (candidates.Count > 1)
            return (null,
                $"Ambiguous: '{name}' matches multiple blueprints: {string.Join(", ", candidates)}. " +
                "Ask the user which one they mean and do not stage anything until they choose.");

        var known = blueprints.OrderBy(k => k);
        return (null, $"Error: no blueprint named '{name}'. Installable blueprints: {string.Join(", ", known)}.");
    }

    /// <summary>
    /// Resolves a model-supplied instance name against the live kgsm list:
    /// exact (case-insensitive) wins; otherwise candidates are gathered by
    /// substring or matching game type. Exactly one candidate resolves; more than
    /// one returns an ambiguity prompt (the model must ask the user, NOT guess);
    /// none returns a miss listing known instances so the model can self-correct.
    /// </summary>
    private async Task<(string? resolved, string? error)> ResolveInstanceAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, "Error: no instance_name was provided.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        if (instances.Count == 0)
            return (null, "Error: there are no installed instances to act on.");

        var query = name.Trim();

        var exact = instances.Keys
            .FirstOrDefault(k => string.Equals(k, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return (exact, null);

        var candidates = instances
            .Where(kv =>
                kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kv.Value, query, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToList();

        if (candidates.Count == 1)
            return (candidates[0], null);

        if (candidates.Count > 1)
            return (null,
                $"Ambiguous: '{name}' matches multiple instances: {string.Join(", ", candidates)}. " +
                "Ask the user which one they mean (list these options) and do not act until they choose.");

        var known = instances.Keys.OrderBy(k => k);
        return (null, $"Error: no instance named '{name}'. Known instances: {string.Join(", ", known)}.");
    }

    /// <summary>
    /// Like <see cref="ResolveInstanceAsync"/>, but a blank/omitted name is valid — it means
    /// "every server" (fleet-wide), returned as <see langword="null"/> with no error. A NON-blank
    /// name still goes through full resolution (exact/substring/game-type, ambiguity refused), so a
    /// typo'd instance_name is caught rather than silently falling back to the whole fleet. Used by
    /// <c>get_audit_log</c>/<c>get_change_timeline</c>, whose <c>instance_name</c> is optional.
    /// </summary>
    private Task<(string? resolved, string? error)> ResolveOptionalInstanceAsync(string? name, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.FromResult<(string?, string?)>((null, null))
            : ResolveInstanceAsync(name, cancellationToken);

    /// <summary>Server-side cap on rows fetched per <c>get_audit_log</c>/<c>get_change_timeline</c>
    /// call — generous enough to cover a week of normal activity; the monitor's own cap (1000) is the
    /// hard ceiling. Filtering (change-timeline) happens client-side on top of this fetch.</summary>
    private const int EventFetchLimit = 200;
}
