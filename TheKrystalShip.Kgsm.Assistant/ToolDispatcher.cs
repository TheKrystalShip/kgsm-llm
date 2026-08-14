using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Consoles;
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
/// set_config — is propose-only: the dispatcher resolves and STAGES it into the
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
    private readonly IServerFacts _serverFacts;
    private readonly IHostFacts _hostFacts;
    private readonly IBlueprintAuthoring _blueprintAuthoring;
    private readonly SettlementTiming _settlement;
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
        IServerFacts serverFacts,
        IHostFacts hostFacts,
        IBlueprintAuthoring blueprintAuthoring,
        SettlementTiming settlement,
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
        _serverFacts = serverFacts;
        _hostFacts = hostFacts;
        _blueprintAuthoring = blueprintAuthoring;
        _settlement = settlement;
        _logger = logger;
    }

    public async Task<ToolOutput> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Dispatching tool '{Tool}' args={Args}",
            call.Name, string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            if (call.Name == LlmTools.ServerInfo)
                return await ServerInfoAsync(call, cancellationToken);
            if (call.Name == LlmTools.HostInfo)
                return await HostInfoAsync(call, cancellationToken);
            if (call.Name == LlmTools.BlueprintInfo)
                return await BlueprintInfoAsync(call, cancellationToken);
            if (call.Name == LlmTools.RunHealthCheck)
                return await RunHealthCheckAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetPerformance)
                return await GetPerformanceAsync(call, cancellationToken);
            if (call.Name == LlmTools.GetNetwork)
                return await GetNetworkAsync(call, cancellationToken);
            if (call.Name == LlmTools.Events)
                return await EventsAsync(call, cancellationToken);
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
            if (call.Name == LlmTools.FindFiles)
                return await FindFilesAsync(call, cancellationToken);
            if (call.Name == LlmTools.SearchFiles)
                return await SearchFilesAsync(call, cancellationToken);
            if (call.Name == LlmTools.ReadConsole)
                return await ReadConsoleAsync(call, cancellationToken);
            if (call.Name == LlmTools.ServerCommand)
                return await StageServerCommandAsync(call, cancellationToken);
            if (call.Name == LlmTools.BackupCommand)
                return await StageBackupCommandAsync(call, cancellationToken);
            if (call.Name == LlmTools.PlayerCommand)
                return await StagePlayerCommandAsync(call, cancellationToken);
            if (call.Name == LlmTools.UninstallServer)
                return await StageUninstallAsync(call, cancellationToken);
            if (call.Name == LlmTools.InstallServer)
                return await StageInstallAsync(call, cancellationToken);
            if (call.Name == LlmTools.SetConfigValue)
                return await StageSetConfigAsync(call, cancellationToken);
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

    /// <summary>
    /// The unified knowledge lookup via the <see cref="ISearch"/> aggregator: local
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

        // Anything unrecognised means "wherever you think best" — the same fail-open the reply style
        // takes. A misread scope must not turn a search into an error.
        SearchScope asked = call.Arg("scope")?.Trim().ToLowerInvariant() switch
        {
            "web" => SearchScope.Web,
            "local" => SearchScope.Local,
            _ => SearchScope.Auto,
        };

        // ⚠ What the person said beats what the model passed. The scope on the call is the model's
        // reading of the request; the turn's intent IS the request, taken from their own words. They
        // disagree in exactly the measured case: asked plainly to check online, the model called
        // search with no scope at all and the local docs answered instead.
        SearchScope scope = SearchIntent.Required ?? asked;

        if (scope != asked)
            _logger.LogDebug(
                "Search scope raised to {Scope} for \"{Query}\": the user asked for it outright",
                scope, query);

        // Recorded before the search runs, so it counts as having looked even when nothing was found.
        // The review this feeds asks whether the assistant LOOKED, not whether it succeeded — an
        // honest "I searched and found nothing" is a real answer to "look it up online".
        SearchIntent.NoteSearched();

        // The aggregator returns the model's grounding text (Summary) plus the cited passages (Data).
        // Attach the surface card only when there is something to cite — an empty / "couldn't search"
        // outcome stays summary-only, exactly like a summary-only lifecycle read (mirrors run_health_check).
        var result = await _search.SearchAsync(query, scope, cancellationToken);
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
    /// The <c>create_blueprint</c> authoring pipeline , run entirely by
    /// <see cref="IBlueprintAuthoring"/> — this handler only validates the argument and relays. Unlike
    /// every command tool, this is NOT staged: it is authorized-and-autonomous (see <see cref="LlmTools.AuthorizedActions"/>),
    /// so it runs to completion here and returns the real outcome. Always carries a card (even a
    /// "couldn't do this one" outcome is worth showing — mirrors <see cref="EventsAsync"/>).
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
    /// Merged status read: no instance_name → a single
    /// fleet-wide summary (the one-shot replacement for fanning a per-instance
    /// liveness loop, which is the agent-loop iteration-cap cause); an
    /// instance_name → detailed status for that one server.
    /// <para>
    /// Only the fleet mode carries a structured card: it has structured data
    /// (<see cref="FleetStatusEntry"/>[]). The single-server mode returns kgsm's opaque status
    /// string — no structured source — so it stays summary-only (a card would be fabricated).
    /// </para>
    /// </summary>
    /// <summary>
    /// The per-instance read. <c>aspect</c> defaults to <c>status</c>, so a bare
    /// <c>server_info(instance)</c> behaves exactly as the old single-purpose status tool did — the
    /// enum is opt-in, which is what keeps the most-called tool's routing intact while the other
    /// aspects replace tools the model used to have to choose between.
    /// </summary>
    private async Task<ToolOutput> ServerInfoAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var aspect = call.Arg("aspect")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(aspect))
            aspect = "status";

        if (!LlmTools.ServerInfoAspects.Contains(aspect))
            return $"Error: '{aspect}' is not a known aspect. "
                 + $"Valid aspects: {string.Join(", ", LlmTools.ServerInfoAspects)}.";

        var name = call.Arg("instance_name")?.Trim();

        // The whole-host reads answer for every instance in one call, so they run before any
        // per-instance resolution — asking them per instance would be N round-trips for one map.
        if (string.IsNullOrWhiteSpace(name))
        {
            return aspect switch
            {
                "status" => await GetFleetStatusAsync(cancellationToken),
                "players" => await PresenceAsync(null, cancellationToken),
                "autostart" => await AutostartAsync(null, cancellationToken),
                _ => $"Error: '{aspect}' needs an instance_name — it reports on one server.",
            };
        }

        var (resolved, error) = await ResolveInstanceAsync(name, cancellationToken);
        if (error is not null)
            return error;

        return aspect switch
        {
            "status" => await SingleStatusAsync(resolved!, cancellationToken),
            // The engine's configuration SUMMARY, never the raw .config.ini. server_info is offered to
            // every caller, and reading a server's files is gated to authorized ones — routing this at
            // read_file would hand the open tier a capability the gate exists to withhold.
            "config" => await ConfigSummaryAsync(resolved!, cancellationToken),
            "version" => await VersionAsync(resolved!, cancellationToken),
            "players" => await PresenceAsync(resolved, cancellationToken),
            "backups" => await BackupsAsync(resolved!, cancellationToken),
            "note" => await NoteAsync(resolved!, cancellationToken),
            "autostart" => await AutostartAsync(resolved, cancellationToken),
            _ => $"Error: '{aspect}' is not a known aspect.",
        };
    }

    private async Task<ToolOutput> SingleStatusAsync(string resolved, CancellationToken cancellationToken)
    {
        var result = await _operations.GetStatusAsync(resolved, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not get status for '{resolved}' ({result.Error ?? "unknown error"}).";

        return $"Status for {resolved}:\n{result.Value}";
    }

    private async Task<ToolOutput> VersionAsync(string resolved, CancellationToken cancellationToken)
    {
        var facts = await _serverFacts.GetVersionAsync(resolved, cancellationToken);
        if (facts.State == FactsState.Unavailable)
            return $"Couldn't read {resolved}'s version — the engine didn't answer. "
                 + "That isn't the same as it being up to date.";

        var installed = facts.Installed ?? "unknown";
        var update = facts.UpdateAvailable switch
        {
            true => $"An update IS available (latest: {facts.Latest ?? "unknown"}).",
            false => "It is up to date.",
            // The engine did not manage a comparison; saying "up to date" here would invent one.
            null => "Whether an update is available could not be checked.",
        };
        var when = facts.CheckedAt is { } at ? $" Checked {at:yyyy-MM-dd HH:mm} UTC." : string.Empty;
        return $"{resolved} is running version {installed}. {update}{when}";
    }

    /// <summary>
    /// Player presence, for one instance or every one. The detection qualifier travels with the
    /// roster: an empty list only means "nobody is connected" when the supervisor can actually
    /// observe this game, and rendering the other case as "0 online" would state something the host
    /// does not know.
    /// </summary>
    private async Task<ToolOutput> PresenceAsync(string? resolved, CancellationToken cancellationToken)
    {
        var reading = await _serverFacts.GetPresenceAsync(cancellationToken);
        if (reading.State == FactsState.Unavailable)
            return "Couldn't read who's online — the supervisor didn't answer. "
                 + "That isn't the same as nobody being connected.";

        var rows = resolved is null
            ? reading.Instances
            : reading.Instances
                .Where(i => string.Equals(i.Instance, resolved, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (rows.Count == 0)
            return resolved is null
                ? "The supervisor is tracking no servers, so there is nobody to report."
                : $"The supervisor isn't tracking '{resolved}', so who's connected is unknown.";

        var lines = rows.Select(r =>
        {
            if (!r.IsMeasured)
                return $"- {r.Instance}: this game doesn't report connected players, so who's on it is unknown.";
            if (r.Players.Count == 0)
                return $"- {r.Instance}: nobody connected.";
            var who = r.Players.Select(p => p.Name ?? p.Id ?? "(unnamed)");
            return $"- {r.Instance}: {r.Players.Count} connected — {string.Join(", ", who)}.";
        });

        return "Connected players:\n" + string.Join("\n", lines);
    }

    private async Task<ToolOutput> BackupsAsync(string resolved, CancellationToken cancellationToken)
    {
        var listing = await _serverFacts.GetBackupsAsync(resolved, cancellationToken);
        if (listing.State == FactsState.Unavailable)
            return $"Couldn't list {resolved}'s backups — the engine didn't answer. "
                 + "That isn't the same as it having none.";

        if (listing.Backups.Count == 0)
            return $"{resolved} has no backups.";

        var lines = listing.Backups.Select(b =>
        {
            var when = b.CreatedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "date unknown";
            var version = b.Version is null ? string.Empty : $", version {b.Version}";
            var size = b.SizeBytes > 0 ? $", {b.SizeBytes / (1024.0 * 1024.0):F1} MB" : string.Empty;
            return $"- {b.Id} ({when}{version}{size})";
        });

        return $"Backups for {resolved}, most recent first:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// The engine's own view of an instance's configuration. Deliberately the status read rather than
    /// the <c>.config.ini</c> itself: this aspect is reachable by every caller, and file contents stay
    /// behind the authorized <c>read_file</c>.
    /// </summary>
    private async Task<ToolOutput> ConfigSummaryAsync(string resolved, CancellationToken cancellationToken)
    {
        var result = await _operations.GetStatusAsync(resolved, cancellationToken);
        return result.IsSuccess
            ? $"Configuration for {resolved}:\n{result.Value}"
            : $"Couldn't read {resolved}'s configuration ({result.Error ?? "unknown error"}).";
    }

    private async Task<ToolOutput> NoteAsync(string resolved, CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        if (!instances.ContainsKey(resolved))
            return $"Error: '{resolved}' is not a known server.";

        var result = await _operations.GetStatusAsync(resolved, cancellationToken);
        return result.IsSuccess
            ? $"Status for {resolved} (its note, if set, appears here):\n{result.Value}"
            : $"Couldn't read {resolved}'s note ({result.Error ?? "unknown error"}).";
    }

    private async Task<ToolOutput> AutostartAsync(string? resolved, CancellationToken cancellationToken)
    {
        var reading = await _serverFacts.GetAutostartAsync(cancellationToken);
        if (reading.State == FactsState.Unavailable)
            return "Couldn't read which servers start at boot — the supervisor didn't answer.";

        if (resolved is not null)
        {
            var on = reading.EnabledInstances.Any(
                n => string.Equals(n, resolved, StringComparison.OrdinalIgnoreCase));
            return on
                ? $"{resolved} IS set to start when the host boots."
                : $"{resolved} is NOT set to start when the host boots.";
        }

        return reading.EnabledInstances.Count == 0
            ? "No servers are set to start when the host boots."
            : "Set to start at boot:\n" + string.Join("\n", reading.EnabledInstances.Select(n => $"- {n}"));
    }

    /// <summary>The host machine's own vitals and port usage — never any one server's.</summary>
    private async Task<ToolOutput> HostInfoAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var aspect = call.Arg("aspect")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(aspect))
            aspect = "vitals";

        if (aspect is "ports" or "conflicts")
        {
            var usage = await _hostFacts.GetPortUsageAsync(cancellationToken);
            if (usage.State == FactsState.Unavailable)
                return "Couldn't read the host's port usage — the engine didn't answer. "
                     + "That isn't the same as nothing being bound.";

            if (aspect == "conflicts")
                return usage.Conflicts.Count == 0
                    ? "No port conflicts between the configured servers."
                    : "Port conflicts:\n" + string.Join("\n", usage.Conflicts.Select(c => $"- {c}"));

            return usage.UsedPorts.Count == 0
                ? "Nothing is currently bound on the host's ports."
                : "Ports in use on the host:\n" + string.Join("\n", usage.UsedPorts.Select(p => $"- {p}"));
        }

        if (aspect != "vitals")
            return $"Error: '{aspect}' is not a known aspect. "
                 + $"Valid aspects: {string.Join(", ", LlmTools.HostInfoAspects)}.";

        var facts = await _hostFacts.GetAsync(cancellationToken);
        if (facts.State == FactsState.Unavailable)
            return "Couldn't read the host's vitals — the engine didn't answer.";

        var parts = new List<string>();
        if (facts.Uptime is not null) parts.Add($"Uptime: {facts.Uptime}");
        if (facts.Load is not null)
            parts.Add($"Load (1/5/15 min): {facts.Load.OneMin} / {facts.Load.FiveMin} / {facts.Load.FifteenMin}");
        if (facts.Memory is not null)
            parts.Add($"Memory: {facts.Memory.Used} used of {facts.Memory.Total} ({facts.Memory.Available} available)");
        if (facts.Disk is not null)
        {
            var pct = facts.Disk.UsedPercent is { } p ? $"{p}% used" : "usage unknown";
            parts.Add($"Disk{(facts.Disk.Mount is null ? "" : $" ({facts.Disk.Mount})")}: "
                    + $"{pct}, {facts.Disk.Available ?? "unknown"} free of {facts.Disk.Size ?? "unknown"}");
        }
        if (facts.ExternalIp is not null) parts.Add($"External IP: {facts.ExternalIp}");
        if (facts.RebootRequired is { } reboot)
            parts.Add(reboot ? "A reboot is pending." : "No reboot pending.");

        return parts.Count == 0
            ? "The host reported no vitals."
            : "Host:\n" + string.Join("\n", parts.Select(p => $"- {p}"));
    }

    /// <summary>
    /// The catalog read: every installable game type, or one game type's detail. Replaces a bare
    /// name list — "what does this game need?" was previously unanswerable without a web search.
    /// </summary>
    private async Task<ToolOutput> BlueprintInfoAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var name = call.Arg("blueprint_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var blueprints = await _inventory.GetBlueprintCatalogAsync(cancellationToken);
            if (blueprints.Count == 0)
                return "There are no installable blueprints.";

            return "Installable game types:\n"
                 + string.Join("\n", blueprints
                     .Select(b => b.Label)
                     .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                     .Select(l => $"- {l}"));
        }

        // The catalog is listed by game name, so a game name is what comes back here — resolve it to the
        // blueprint the engine knows before asking the engine about it.
        var (resolved, resolveError) = await ResolveBlueprintAsync(name, cancellationToken);
        if (resolveError is not null)
            return resolveError;

        var detail = await _inventory.GetBlueprintDetailAsync(resolved!, cancellationToken);
        if (detail is null)
            return $"'{name}' is not a known game type. Use blueprint_info with no name to list them.";

        var parts = new List<string> { $"Game type: {detail.DisplayName ?? detail.Name} ({detail.Kind})" };
        if (detail.Description is not null) parts.Add(detail.Description);
        if (detail.Ports.Count > 0) parts.Add($"Ports: {string.Join(", ", detail.Ports)}");
        if (detail.MaxPlayers is { } players) parts.Add($"Max players: {players}");
        if (detail.MinRamMb is { } min) parts.Add($"Minimum RAM: {min} MB");
        if (detail.RecommendedRamMb is { } rec) parts.Add($"Recommended RAM: {rec} MB");
        if (detail.BaseDiskMb is { } disk) parts.Add($"Base disk: {disk} MB");
        if (detail.SteamAccountRequired) parts.Add("Requires a Steam account to install.");
        parts.Add(detail.ModerationVerbs.Count == 0
            ? "This game's server supports no player moderation commands."
            : $"Player moderation supported: {string.Join(", ", detail.ModerationVerbs)}.");

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Locates files by glob anywhere under the instance's directory, so the model can reach a config
    /// that sits several levels down in one call instead of descending a directory at a time.
    /// <para>
    /// The two bounded outcomes are worded differently on purpose. Truncation invites a narrower
    /// pattern; an incomplete walk is stated as "I stopped looking", because letting the model narrate
    /// that as "there is no such file" is the fabrication this whole surface exists to avoid.
    /// </para>
    /// </summary>
    private async Task<ToolOutput> FindFilesAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var pattern = call.Arg("pattern")?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: find_files needs a 'pattern' — the file name or glob to look for.";

        var result = await _operations.FindInstanceFilesAsync(
            resolved!, pattern, NullIfBlank(call.Arg("subdir")), cancellationToken);
        if (!result.IsSuccess)
            return $"Couldn't search '{resolved}' ({result.Error ?? "unknown error"}).";

        var matches = result.Value!;
        if (matches.Paths.Count == 0)
        {
            return matches.Incomplete
                ? $"No match for '{pattern}' in the part of {resolved} that was searched — the search " +
                  "stopped before covering everything, so this does NOT mean the file isn't there. " +
                  "Try a subdir to narrow where to look."
                : $"No file matching '{pattern}' in {resolved}.";
        }

        var lines = string.Join("\n", matches.Paths.Select(p => $"- {p}"));
        var note = matches.Truncated
            ? $"\n(More files matched than are shown — narrow the pattern if none of these is right.)"
            : matches.Incomplete
                ? "\n(The search stopped before covering the whole folder, so there may be more.)"
                : string.Empty;

        return $"Files in {resolved} matching '{pattern}':\n{lines}{note}";
    }

    /// <summary>
    /// Searches file contents for a pattern — the counterpart to <see cref="FindFilesAsync"/> for when
    /// the model knows the setting but not the file. Same honesty rule on the two bounded outcomes:
    /// truncation invites a narrower pattern, an incomplete walk is never "there is no such setting".
    /// </summary>
    private async Task<ToolOutput> SearchFilesAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        // Accept "pattern" as well: it is find_files' argument name, and a model that has just used
        // that tool reaches for the same word. Taking it costs nothing and saves a whole turn.
        var pattern = (call.Arg("text") ?? call.Arg("pattern"))?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: search_files needs 'text' — the text to look for inside the files.";

        // A glob here is a routing slip, not a search that found nothing: "*Player*" is a valid
        // FILENAME pattern and an invalid expression, so relaying the regex parser's complaint sends
        // the model round again with another glob. Name the mistake and the fix instead.
        if (InvalidExpression(pattern) is { } why)
            return $"Error: '{pattern}' isn't valid here — {why} 'text' is the text to find INSIDE " +
                   "the files, not a filename pattern, so it takes no \"*\" wildcards. Search for the " +
                   $"bare string (e.g. \"{pattern.Trim('*', '?', '.')}\"), or use find_files if you " +
                   "are looking for a file by NAME.";

        var result = await _operations.SearchInstanceFilesAsync(
            resolved!, pattern, NullIfBlank(call.Arg("subdir")), ignoreCase: true, cancellationToken);
        if (!result.IsSuccess)
            return $"Couldn't search '{resolved}' ({result.Error ?? "unknown error"}).";

        var matches = result.Value!;
        if (matches.Matches.Count == 0)
        {
            // A search covering everything and matching nothing means this game does not spell the
            // setting that way — so re-running a near-identical spelling burns the turn without ever
            // changing the answer. Say what actually works: a shorter fragment, since games name the
            // same knob differently ("max players" can be stored as ServerPlayerMaxNum).
            return matches.Incomplete
                ? $"No match for '{pattern}' in the part of {resolved} that was searched — the search " +
                  "stopped before covering everything, so this does NOT mean the setting isn't there."
                : $"Nothing in {resolved}'s files matches '{pattern}'. The whole folder was searched, " +
                  "so a near-identical spelling will not match either — this game names it something " +
                  "else. Search a SHORTER distinctive fragment of the word instead, or locate the " +
                  "config with find_files and read it.";
        }

        var lines = string.Join("\n", matches.Matches.Select(m => $"- {m.Path}:{m.Line}: {Clip(m.Text)}"));
        var note = matches.Truncated
            ? "\n(More lines matched than are shown — narrow the pattern if none of these is right.)"
            : matches.Incomplete
                ? "\n(The search stopped before covering the whole folder, so there may be more.)"
                : string.Empty;

        return $"Matches for '{pattern}' in {resolved}:\n{lines}{note}";
    }

    /// <summary>
    /// Why this expression won't compile, or <see langword="null"/> when it will. Checked here rather
    /// than left to the search itself so the model gets one message naming the argument it confused,
    /// instead of the regex parser's "Quantifier '*' is not preceded by a valid expression" — which
    /// reads as a failed search and earns a retry with the same mistake.
    /// </summary>
    private static string? InvalidExpression(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message.TrimEnd('.') + ".";
        }
    }

    /// <summary>Keeps one matched line short enough that a wide result set still fits the model's context.</summary>
    private static string Clip(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    /// <summary>
    /// Reads one run of the supervisor's captured console output for an instance.
    /// <para>
    /// <b>The output is prefaced with which run it is.</b> A server's log restarts from empty on every
    /// fresh start, so after a crash-restart the default run holds a clean boot and the crash is in the
    /// run before it. Those lines look identical to a healthy server's, and the model sees only this
    /// text — so where they came from has to be said here, in the same string, or it is not said at
    /// all. <see cref="ConsoleProvenance"/> owns the wording.
    /// </para>
    /// </summary>
    private async Task<ToolOutput> ReadConsoleAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var lines = int.TryParse(call.Arg("lines")?.Trim(), out var n) && n > 0
            ? Math.Min(n, MaxConsoleLines)
            : DefaultConsoleLines;

        var run = int.TryParse(call.Arg("run")?.Trim(), out var r) && r > 0 ? r : CurrentConsoleRun;

        // The run list is what places the output; it is fetched alongside rather than instead, so a
        // supervisor that serves the lines but not the list still answers with the lines.
        var runs = await _serverFacts.GetConsoleRunsAsync(resolved!, cancellationToken);

        if (run != CurrentConsoleRun && runs.State == FactsState.Available && run >= runs.Runs.Count)
            return runs.Runs.Count == 0
                ? $"{resolved} has no recorded runs, so there is no run {run} to read."
                : $"{resolved} has no run {run} — it has {runs.Runs.Count} run(s) on record, numbered 0 "
                  + $"(most recent) to {runs.Runs.Count - 1}.";

        var tail = await _serverFacts.GetConsoleRunTailAsync(resolved!, lines, run, cancellationToken);
        if (tail.State == FactsState.Unavailable)
            return $"Couldn't read {resolved}'s console — the supervisor didn't answer. "
                 + "That isn't the same as it having produced no output.";

        if (tail.Lines.Count == 0)
            return run == CurrentConsoleRun
                ? $"{resolved} has produced no console output in its current run."
                : $"{resolved}'s run {run} holds no output.";

        string header = ConsoleProvenance.Describe(resolved!, runs.Runs, run, DateTimeOffset.UtcNow);
        return header + "\n" + string.Join("\n", tail.Lines);
    }

    /// <summary>The run in progress — what a caller that named no run means.</summary>
    private const int CurrentConsoleRun = 0;

    private const int DefaultConsoleLines = 50;
    private const int MaxConsoleLines = 500;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// One-shot fleet status: fetches the neutral entries and returns a
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
    /// The first aggregator: resolves the instance, fetches the
    /// neutral health inputs via the port, and runs the deterministic
    /// <see cref="HealthCheckAggregator"/>. Returns a <see cref="ToolOutput"/> whose
    /// <c>Summary</c> is the model's grounding text AND whose <c>Data</c> carries the
    /// structured <see cref="ToolResultCard"/> for a streaming surface — the
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
    /// The unfiltered engine-event read: resolves an OPTIONAL instance (blank →
    /// every server on this host), maps the model's <c>window</c> to a <c>since</c> bound, reads the
    /// engine's raw event journal via <see cref="IEventHistory"/> directly (never via kgsm-api),
    /// and runs the pure <see cref="AuditReport"/>. Always carries a card: an empty/unavailable result
    /// is still a real, honestly-worded answer worth showing, unlike a not-running metrics snapshot.
    /// </summary>
    /// <summary>
    /// The engine's event history. One tool over both scopes: the unfiltered feed and the
    /// state-changing subset read the SAME journal and differ only in which rows they keep (see
    /// <see cref="AuditReport.ChangeEventTypes"/>) and how far back they default to. They were two
    /// tools whose descriptions both plausibly matched "when was X updated?", which is exactly the
    /// overlap a small model routes badly — the scope enum makes it one decision instead of two.
    /// </summary>
    private async Task<ToolOutput> EventsAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOptionalInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var changes = string.Equals(call.Arg("scope")?.Trim(), "changes", StringComparison.OrdinalIgnoreCase);
        var fallback = changes ? AuditWindow.DefaultChangeRange : AuditWindow.DefaultAuditWindow;
        var (window, sinceMs) = AuditWindow.Resolve(call.Arg("window"), fallback, DateTimeOffset.UtcNow);

        var reading = await _events.GetEventsAsync(resolved, sinceMs, EventFetchLimit, cancellationToken);
        var result = changes
            ? AuditReport.BuildChangeTimeline(reading, resolved, window)
            : AuditReport.Build(reading, resolved, window);
        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// The capstone aggregator: resolves the REQUIRED instance (root cause
    /// is always about one server), then fetches the three sources <see cref="RootCauseAggregator"/>
    /// composes — the event timeline (<see cref="IEventHistory"/>), a metrics window
    /// (<see cref="IServerMetrics"/>), and the health snapshot (<see cref="IServerOperations.GetHealthSnapshotAsync"/>,
    /// the same neutral input <see cref="RunHealthCheckAsync"/> uses) — IN PARALLEL, so a slow source
    /// doesn't serialize the read. Each source degrades independently and honestly: a failed health
    /// snapshot passes <see langword="null"/> + its error into the aggregator rather than refusing the
    /// whole call; the aggregator's rules table then simply can't evaluate the rules that need it. No
    /// nested model call happens anywhere in this path — <see cref="RootCauseAggregator.Run"/> is pure.
    /// Always attaches a card: even a "nothing matched" correlation or a fully-degraded read is a real,
    /// honestly-worded answer worth showing (mirrors <see cref="EventsAsync"/>).
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
        // The run list doesn't depend on the timeline, so it rides along with the other three; only
        // choosing WHICH run to read has to wait for the crash's timestamp.
        var runsTask = _serverFacts.GetConsoleRunsAsync(resolved!, cancellationToken);
        await Task.WhenAll(eventsTask, metricsTask, healthTask, runsTask);

        var crashConsole = await ReadCrashConsoleAsync(
            resolved!, eventsTask.Result, runsTask.Result, cancellationToken);

        var healthResult = healthTask.Result;
        var result = RootCauseAggregator.Run(
            resolved!, range, eventsTask.Result, metricsTask.Result,
            health: healthResult.IsSuccess ? healthResult.Value : null,
            healthUnavailableReason: healthResult.IsSuccess ? null : healthResult.Error,
            crashConsole: crashConsole);

        return new ToolOutput(result.Summary, ToolResultCard.From(result));
    }

    /// <summary>
    /// Reads what the crashed run printed — the source that answers "why" when the others only say
    /// "when". The log rotates on every fresh start, so after a crash-restart the live console holds
    /// a clean boot and the cause sits in the run that ended; this fetches that one.
    /// <para>
    /// Orchestration only. Which run belongs to the crash is decided by the pure
    /// <see cref="CrashRunSelector"/>, so the pairing rule lives beside the rules that consume it
    /// instead of in the code that happens to do the I/O.
    /// </para>
    /// <para>
    /// Degrades like every other source: no crash in the window, no run matching it, or an
    /// unavailable supervisor each produce an honest empty rather than failing the trace — and the
    /// last of those is reported as unavailable, never as the run having printed nothing.
    /// </para>
    /// </summary>
    private async Task<CrashConsole> ReadCrashConsoleAsync(
        string instance, EventHistoryReading events, ConsoleRuns runs, CancellationToken cancellationToken)
    {
        // The most recent crash: the one a person asking "why did it crash" means.
        var crash = events.Events
            .Where(e => e.Type is "instance_crashed")
            .OrderByDescending(e => e.Ts)
            .FirstOrDefault();

        if (crash is null)
            return CrashConsole.NoCrash;

        if (runs.State == FactsState.Unavailable)
            return new CrashConsole(crash, [], FactsState.Unavailable);

        var index = CrashRunSelector.Select(runs.Runs, crash.Ts);
        if (index is null)
            // The runs were listed and none of them ended near the crash — a real answer (the output
            // has aged out, or this instance's console isn't the supervisor's to keep), not a failure.
            return new CrashConsole(crash, [], FactsState.Available);

        var tail = await _serverFacts.GetConsoleRunTailAsync(
            instance, CrashConsoleLines, index.Value, cancellationToken);

        // The exit code belongs to the run that was selected, so it is read from that run's own entry
        // rather than looked up again — a second lookup could resolve differently and describe a
        // different run's ending than the output being quoted.
        int? exitCode = runs.Runs.FirstOrDefault(r => r.Index == index.Value)?.ExitCode;

        return new CrashConsole(crash, tail.Lines, tail.State, exitCode);
    }

    /// <summary>
    /// How much of the crashed run to read. Deep enough that a stack trace's origin is still in view
    /// after the frames below it; the aggregator quotes a bounded excerpt of what comes back.
    /// </summary>
    private const int CrashConsoleLines = 120;

    /// <summary>
    /// The merged lifecycle command: maps the model-supplied <c>verb</c>
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
    /// Acting on an instance's EXISTING backups. Restore and delete name a specific archive; prune
    /// takes a keep-count instead. The archive id is NOT resolved against a listing here — the engine
    /// owns which ids exist, and inventing a "closest match" to a mistyped id is how the wrong backup
    /// gets restored over live data.
    /// </summary>
    private async Task<string> StageBackupCommandAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var verb = call.Arg("verb");
        var kind = LlmTools.BackupCommandKind(verb);
        if (kind is null)
            return $"Error: '{verb ?? "(none)"}' is not a valid backup action. " +
                   $"Valid actions: {string.Join(", ", LlmTools.BackupCommandVerbs)}.";

        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var backupName = call.Arg("backup_name")?.Trim();
        if (kind is ConfirmationKind.BackupRestore or ConfirmationKind.BackupDelete
            && string.IsNullOrWhiteSpace(backupName))
            return $"Error: a {verb} needs 'backup_name' — the id of the backup to act on. " +
                   "List them with server_info(aspect=backups) first.";

        var keep = call.Arg("keep")?.Trim();
        if (kind is ConfirmationKind.BackupPrune
            && !string.IsNullOrWhiteSpace(keep)
            && (!int.TryParse(keep, out var parsed) || parsed < 1))
            return "Error: 'keep' must be a whole number of backups to keep, at least 1.";

        _confirmations.Stage(new PendingConfirmation(
            kind.Value, resolved!, ConfigKey: backupName, ConfigValue: keep));

        return $"Staged a request to {ConfirmationKinds.Verb(kind.Value)} '{resolved}' for confirmation. " +
               "A confirmation prompt with a button has been shown to the user. This is NOT done yet and " +
               "will only run if a permitted human clicks Confirm — tell the user it's awaiting their " +
               "confirmation.";
    }

    /// <summary>
    /// Player moderation. Refused up front for a game whose blueprint declares no command for the
    /// requested verb: the assistant saying "I've proposed a ban" on a game that cannot ban is a
    /// promise the confirm step would then break.
    /// </summary>
    private async Task<string> StagePlayerCommandAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var verb = call.Arg("verb")?.Trim().ToLowerInvariant();
        var kind = LlmTools.PlayerCommandKind(verb);
        if (kind is null)
            return $"Error: '{verb ?? "(none)"}' is not a valid moderation action. " +
                   $"Valid actions: {string.Join(", ", LlmTools.PlayerCommandVerbs)}.";

        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var target = call.Arg("target")?.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return "Error: player moderation needs a 'target' — which player to act on.";

        // Whether this game supports the verb is the blueprint's fact, so ask it rather than staging
        // something the engine will refuse later.
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        if (instances.TryGetValue(resolved!, out var blueprint))
        {
            var detail = await _inventory.GetBlueprintDetailAsync(blueprint, cancellationToken);
            var game = detail?.DisplayName ?? blueprint;
            if (detail is not null && !detail.ModerationVerbs.Contains(verb!))
                return detail.ModerationVerbs.Count == 0
                    ? $"'{resolved}' runs {game}, whose server supports no player moderation " +
                      "commands at all. Tell the user rather than proposing one."
                    : $"'{resolved}' runs {game}, whose server cannot {verb}. It supports: " +
                      $"{string.Join(", ", detail.ModerationVerbs)}. Tell the user rather than proposing one.";
        }

        _confirmations.Stage(new PendingConfirmation(kind.Value, resolved!, ConfigKey: target));

        return $"Staged a request to {ConfirmationKinds.Verb(kind.Value)} '{resolved}' for confirmation. " +
               "A confirmation prompt with a button has been shown to the user. This is NOT done yet and " +
               "will only run if a permitted human clicks Confirm — tell the user it's awaiting their " +
               "confirmation.";
    }

    /// <summary>
    /// The lifecycle command. Default is propose-only: resolves the instance, then
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
    /// <para>
    /// It settles against the observed run state for the same reason the confirm path does, and it
    /// matters more here: this string is what the model reads, and a model told "done, it has been
    /// started" will tell the user the server is up. An unsettled or unreadable outcome is
    /// therefore spelled out as such, so the model reports what is actually known.
    /// </para>
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
            // Not a lifecycle verb → fall back to staging. server_command's autostart verbs land here
            // deliberately: the auto-accept toggle covers acting on a running server, and changing what
            // happens at the next boot is a different intent that keeps its human confirmation.
            _ => null,
        };

        if (op is null)
        {
            _confirmations.Stage(new PendingConfirmation(kind, instance));
            return $"Staged a {ConfirmationKinds.Verb(kind)} of '{instance}' for confirmation — tell the user it's awaiting their confirmation.";
        }

        _logger.LogInformation("Auto-executing {Verb} of {Instance}", ConfirmationKinds.Verb(kind), instance);

        // This turn acts without staging anything, so record that it acted: it is the one path on
        // which a reply reporting the command as done is telling the truth.
        _confirmations.NoteActionPerformed();

        var outcome = await CommandSettlement.RunAndSettleAsync(
            _operations, kind, instance, op, _settlement, cancellationToken: cancellationToken);

        return outcome.Verdict switch
        {
            ConfirmVerdict.Settled or ConfirmVerdict.Accepted => $"Done — {outcome.Summary}",
            _ => outcome.Summary,
        };
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

        // Optional install overrides. A port that isn't a number is refused rather than dropped: an
        // install silently landing on the blueprint's default port is not what was asked for.
        var version = call.Arg("version")?.Trim();
        var port = call.Arg("port")?.Trim();
        if (!string.IsNullOrWhiteSpace(port)
            && (!int.TryParse(port, out var parsedPort) || parsedPort is < 1 or > 65535))
            return $"Error: '{port}' is not a valid port number.";

        _confirmations.Stage(new PendingConfirmation(
            ConfirmationKind.Install, blueprint!, instanceName,
            ConfigKey: NullIfBlank(version), ConfigValue: NullIfBlank(port)));

        var named = instanceName is null ? "" : $" named '{instanceName}'";
        var at = string.IsNullOrWhiteSpace(port) ? "" : $" on port {port}";
        var ver = string.IsNullOrWhiteSpace(version) ? "" : $" at version {version}";
        var game = await GameLabelAsync(blueprint!, cancellationToken);
        return $"Staged an install of a new {game} server{named}{ver}{at} for confirmation. A confirmation " +
               "prompt with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    /// <summary>
    /// Propose-only: resolves the instance and validates a non-empty key, then
    /// STAGES a set-config for human confirmation — it is never written here. kgsm owns
    /// the key-safety policy (denylist); this stage does not pre-judge the key, so a
    /// refusal surfaces only at confirm time. An empty value is allowed (clears the
    /// setting); a missing/blank key short-circuits to the model.
    /// <para>The one key family this stage DOES pre-judge is the server note
    /// (<c>note</c>/<c>note_updated_by</c>/<c>note_updated_at</c>): kgsm accepts them as ordinary
    /// runtime values, but the note is player-facing text with its own surface that owns the encoding
    /// and the attribution stamp. Writing it raw here would put an unencoded body into a file that is
    /// sourced as <c>key="value"</c> and credit the edit to nobody, so a chat turn cannot rewrite a
    /// note.</para>
    /// </summary>
    private async Task<string> StageSetConfigAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var key = call.Arg("config_key")?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return "Error: no config_key was provided.";

        if (IsServerNoteKey(key))
            return "Error: the server note is not editable from chat. It is written from the control " +
                   "panel's server page (the 'Server note' card), which records who wrote it. Tell the " +
                   "user to edit it there.";

        // The value is intentionally NOT trimmed and may be the empty string (clearing
        // the setting). A model that omits it entirely is treated as clearing it.
        var value = call.Arg("config_value") ?? string.Empty;

        _confirmations.Stage(new PendingConfirmation(
            ConfirmationKind.SetConfig, resolved!, InstanceName: null, ConfigKey: key, ConfigValue: value));

        return $"Staged setting '{key}' on '{resolved}' for confirmation. A confirmation prompt " +
               "with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
    }

    // The server note's three config keys. Matched case-insensitively so a model that shouts the key
    // doesn't slip past the gate.
    private static bool IsServerNoteKey(string key) =>
        key.Equals("note", StringComparison.OrdinalIgnoreCase)
        || key.Equals("note_updated_by", StringComparison.OrdinalIgnoreCase)
        || key.Equals("note_updated_at", StringComparison.OrdinalIgnoreCase);

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
    /// Resolves a model-supplied game name to the blueprint the engine knows it as. Both names a
    /// blueprint has are matched — the identifier and the display name — because the catalog the model
    /// reads is written in display names, so that is what comes back: exact (case-insensitive) first,
    /// then equal once punctuation and spacing are set aside ("Counter-Strike: Source" ↔ "counter
    /// strike source"), then a single substring match. What resolves is always the identifier: the
    /// display name is for saying, and everything downstream is staged and executed against the engine's
    /// own word. Ambiguous or unknown returns a message so the model asks the user / self-corrects.
    /// </summary>
    private async Task<(string? resolved, string? error)> ResolveBlueprintAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, "Error: no blueprint_name was provided.");

        var blueprints = await _inventory.GetBlueprintCatalogAsync(cancellationToken);
        if (blueprints.Count == 0)
            return (null, "Error: there are no installable blueprints available.");

        var query = name.Trim();

        var exact = blueprints.FirstOrDefault(b =>
            string.Equals(b.Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.DisplayName, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return (exact.Name, null);

        var normalizedQuery = NormalizeGameName(query);
        if (normalizedQuery.Length > 0)
        {
            var normalized = blueprints
                .Where(b => NormalizeGameName(b.Name) == normalizedQuery
                         || NormalizeGameName(b.Label) == normalizedQuery)
                .ToList();
            if (normalized.Count == 1)
                return (normalized[0].Name, null);
        }

        // One blueprint matching on both of its names is one candidate, not two — the predicate is per
        // blueprint, so a query hitting the identifier AND the display name never reads as ambiguous.
        var candidates = blueprints
            .Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || b.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (normalizedQuery.Length > 0
                         && (NormalizeGameName(b.Name).Contains(normalizedQuery, StringComparison.Ordinal)
                          || NormalizeGameName(b.Label).Contains(normalizedQuery, StringComparison.Ordinal))))
            .OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
            return (candidates[0].Name, null);

        if (candidates.Count > 1)
            return (null,
                $"Ambiguous: '{name}' matches multiple games: {string.Join(", ", candidates.Select(b => b.Label))}. " +
                "Ask the user which one they mean and do not stage anything until they choose.");

        var known = blueprints.Select(b => b.Label).OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
        return (null, $"Error: no game named '{name}' can be installed. Installable games: {string.Join(", ", known)}.");
    }

    /// <summary>
    /// The name to call a blueprint by in text the model reads back to a person. Falls back to the
    /// identifier when the catalog cannot be read or does not carry it — a name is worth having, but
    /// not worth failing a lookup over. Accepts an instance's <c>.bp</c>-suffixed form too.
    /// </summary>
    private async Task<string> GameLabelAsync(string blueprintName, CancellationToken cancellationToken)
    {
        var key = blueprintName.EndsWith(".bp", StringComparison.OrdinalIgnoreCase)
            ? blueprintName[..^3]
            : blueprintName;

        var catalog = await _inventory.GetBlueprintCatalogAsync(cancellationToken);
        return catalog.FirstOrDefault(b => string.Equals(b.Name, key, StringComparison.OrdinalIgnoreCase))?.Label
               ?? blueprintName;
    }

    /// <summary>
    /// A game name reduced to its letters and digits, lowercased — what "Don't Starve Together",
    /// "dont starve together" and <c>dontstarvetogether</c> have in common. Punctuation and spacing are
    /// where a spoken or typed game name differs from the blueprint's own spelling, and neither
    /// difference means a different game.
    /// </summary>
    private static string NormalizeGameName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
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
    /// <c>events</c>, whose <c>instance_name</c> is optional.
    /// </summary>
    private Task<(string? resolved, string? error)> ResolveOptionalInstanceAsync(string? name, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.FromResult<(string?, string?)>((null, null))
            : ResolveInstanceAsync(name, cancellationToken);

    /// <summary>Server-side cap on rows fetched per <c>events</c>
    /// call — generous enough to cover a week of normal activity; the reader's own cap (1000) is the
    /// hard ceiling. Filtering (change-timeline) happens client-side on top of this fetch.</summary>
    private const int EventFetchLimit = 200;
}
