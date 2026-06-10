using Microsoft.Extensions.Logging;

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
/// cached <see cref="IServerInventory"/>; real-time reads and writes go through
/// <see cref="IServerOperations"/>. install/uninstall are resolved and STAGED into
/// the <see cref="IConfirmationContext"/> — never executed here.
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
                LlmTools.ListInstances => await ListInstancesAsync(cancellationToken),
                LlmTools.ListBlueprints => await ListBlueprintsAsync(cancellationToken),
                LlmTools.GetServerStatus => await GetServerStatusAsync(call, cancellationToken),
                LlmTools.IsServerActive => await IsServerActiveAsync(call, cancellationToken),
                LlmTools.StartServer => await ActAsync(call, _operations.StartAsync, "started", cancellationToken),
                LlmTools.StopServer => await ActAsync(call, _operations.StopAsync, "stopped", cancellationToken),
                LlmTools.RestartServer => await ActAsync(call, _operations.RestartAsync, "restarted", cancellationToken),
                LlmTools.CreateBackup => await ActAsync(call, _operations.CreateBackupAsync, "backed up", cancellationToken),
                LlmTools.UpdateServer => await ActAsync(call, _operations.UpdateAsync, "updated", cancellationToken),
                LlmTools.UninstallServer => await StageUninstallAsync(call, cancellationToken),
                LlmTools.InstallServer => await StageInstallAsync(call, cancellationToken),
                _ => $"Error: '{call.Name}' is not a known tool."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{Tool}' threw", call.Name);
            return $"Error: the '{call.Name}' tool failed unexpectedly.";
        }
    }

    private async Task<string> ListInstancesAsync(CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        if (instances.Count == 0)
            return "There are no installed server instances.";

        var lines = instances
            .OrderBy(kv => kv.Key)
            .Select(kv => $"- {kv.Key} (game: {kv.Value})");
        return "Installed instances:\n" + string.Join("\n", lines);
    }

    private async Task<string> ListBlueprintsAsync(CancellationToken cancellationToken)
    {
        var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
        if (blueprints.Count == 0)
            return "There are no installable blueprints.";

        var names = blueprints.OrderBy(k => k);
        return "Installable game types:\n" + string.Join("\n", names.Select(n => $"- {n}"));
    }

    private async Task<string> GetServerStatusAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var result = await _operations.GetStatusAsync(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not get status for '{resolved}' ({result.Error ?? "unknown error"}).";

        return $"Status for {resolved}:\n{result.Value}";
    }

    private async Task<string> IsServerActiveAsync(LlmToolCall call, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var result = await _operations.IsActiveAsync(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not check '{resolved}' ({result.Error ?? "unknown error"}).";

        return result.Value
            ? $"{resolved} is currently running."
            : $"{resolved} is currently stopped.";
    }

    /// <summary>
    /// Resolves the instance name, then runs the given mutating operation. Returns
    /// a human-readable result string either way (resolution problems included).
    /// </summary>
    private async Task<string> ActAsync(
        LlmToolCall call,
        Func<string, CancellationToken, Task<Result>> operation,
        string pastTense,
        CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveInstanceAsync(call.Arg("instance_name"), cancellationToken);
        if (error is not null)
            return error;

        var result = await operation(resolved!, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: could not complete the action on '{resolved}' ({result.Error ?? "unknown error"}).";

        return $"Done: {resolved} has been {pastTense}.";
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
