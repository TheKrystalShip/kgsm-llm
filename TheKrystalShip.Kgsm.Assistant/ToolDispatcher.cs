using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
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
/// </summary>
public class ToolDispatcher : IToolDispatcher
{
    private readonly IServerOperations _operations;
    private readonly IServerInventory _inventory;
    private readonly IConfirmationContext _confirmations;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IServerOperations operations,
        IServerInventory inventory,
        IConfirmationContext confirmations,
        ILogger<ToolDispatcher> logger)
    {
        _operations = operations;
        _inventory = inventory;
        _confirmations = confirmations;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Dispatching tool '{Tool}' args={Args}",
            call.Name, string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            return call.Name switch
            {
                LlmTools.GetStatus => await GetStatusAsync(call, cancellationToken),
                LlmTools.ListBlueprints => await ListBlueprintsAsync(cancellationToken),
                LlmTools.RunHealthCheck => await RunHealthCheckAsync(call, cancellationToken),
                LlmTools.ViewConfigFile => await ViewConfigFileAsync(call, cancellationToken),
                LlmTools.ServerCommand => await StageServerCommandAsync(call, cancellationToken),
                LlmTools.UninstallServer => await StageUninstallAsync(call, cancellationToken),
                LlmTools.InstallServer => await StageInstallAsync(call, cancellationToken),
                LlmTools.SetConfigValue => await StageSetConfigAsync(call, cancellationToken),
                _ => $"Error: '{call.Name}' is not a known tool."
            };
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
    /// Reads an instance's main config file (<c>&lt;name&gt;.config.ini</c>), redacted.
    /// V1 whitelist (§3.8): only that one file. The filename is derived from the
    /// resolved (real-inventory-matched) instance name, so the model supplies no
    /// path segment — there is no attacker-controlled path component. The port
    /// enforces instance-directory path-binding as defense-in-depth.
    /// </summary>
    private async Task<string> ViewConfigFileAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var file = $"{resolved}.config.ini";

        var result = await _operations.ReadInstanceFileAsync(resolved!, file, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not read the config for '{resolved}' ({result.Error ?? "unknown error"}).";

        var redacted = RedactSecrets(result.Value ?? string.Empty);
        return $"Config file ({file}) for {resolved}:\n{redacted}";
    }

    /// <summary>Secret-key hints for light V1 redaction. Kept tight on purpose —
    /// over-redaction would hide the very settings a user is trying to fix.</summary>
    private static readonly string[] SecretKeyHints = ["password", "passwd", "secret", "token"];

    /// <summary>
    /// Masks the value of any <c>key = value</c> / <c>key: value</c> line whose KEY
    /// contains a secret hint, leaving everything else intact. Matching on the key
    /// (not the value) avoids mangling unrelated content.
    /// </summary>
    private static string RedactSecrets(string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var sep = lines[i].IndexOfAny(['=', ':']);
            if (sep <= 0)
                continue;

            var key = lines[i][..sep];
            if (SecretKeyHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase)))
                lines[i] = lines[i][..(sep + 1)] + " ***redacted***";
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Merged status read (toolbox catalog §4.1): no instance_name → a single
    /// fleet-wide summary (the one-shot replacement for fanning a per-instance
    /// liveness loop, which is the agent-loop iteration-cap cause); an
    /// instance_name → detailed status for that one server.
    /// </summary>
    private async Task<string> GetStatusAsync(LlmToolCall call, CancellationToken cancellationToken)
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
    /// One-shot fleet status. An instance whose status could not be read is
    /// reported as "status unavailable (reason)", never collapsed to "stopped" —
    /// the model must not narrate a read failure as a measured state.
    /// </summary>
    private async Task<string> GetFleetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _operations.GetFleetStatusAsync(cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not read server status ({result.Error ?? "unknown error"}).";

        var entries = result.Value!;
        if (entries.Count == 0)
            return "There are no installed server instances.";

        var lines = entries.Select(e => e.Availability == FleetStatusAvailability.Read
            ? $"- {e.Instance}: {(e.Running == true ? "running" : "stopped")}"
            : $"- {e.Instance}: status unavailable ({e.Reason ?? "unknown reason"})");

        return $"Status of all {entries.Count} server(s):\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// The first aggregator (toolbox-plan §3.4): resolves the instance, fetches the
    /// neutral health inputs via the port, and runs the deterministic
    /// <see cref="HealthCheckAggregator"/>. Returns the result's <c>Summary</c> — the
    /// model's grounding text (§3.6); the structured card travels to a surface later.
    /// All judgment lives in the aggregator, so this handler only orchestrates.
    /// </summary>
    private async Task<string> RunHealthCheckAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var result = await _operations.GetHealthSnapshotAsync(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not run a health check on '{resolved}' ({result.Error ?? "unknown error"}).";

        return HealthCheckAggregator.Run(result.Value!, resolved!).Summary;
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
    /// Propose-only (§3.5): resolves the instance, then STAGES the command for human
    /// confirmation instead of executing it — the same path uninstall/install already
    /// take. Resolution problems (ambiguous / unknown) short-circuit to the model so it
    /// asks the user, and nothing is staged for an unresolved target. The single-instance
    /// op itself (<c>StartAsync</c> et al.) runs later, from
    /// <see cref="ServerAssistant.ConfirmAsync"/>, only after a human confirms.
    /// </summary>
    private async Task<string> StageCommandAsync(
        LlmToolCall call, ConfirmationKind kind, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        _confirmations.Stage(new PendingConfirmation(kind, resolved!));

        return $"Staged a {ConfirmationKinds.Verb(kind)} of '{resolved}' for confirmation. A confirmation " +
               "prompt with a button has been shown to the user. This is NOT done yet and will only run " +
               "if a permitted human clicks Confirm — tell the user it's awaiting their confirmation.";
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
