using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
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
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IServerOperations operations,
        IServerInventory inventory,
        IConfirmationContext confirmations,
        ISearch search,
        ILogger<ToolDispatcher> logger)
    {
        _operations = operations;
        _inventory = inventory;
        _confirmations = confirmations;
        _search = search;
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
            if (call.Name == LlmTools.Search)
                return await SearchAsync(call, cancellationToken);
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
    private async Task<string> SearchAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var query = call.Arg("query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return "Error: search needs a 'query'.";

        return await _search.SearchAsync(query, cancellationToken);
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
}
