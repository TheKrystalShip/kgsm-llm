using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Assembles a kgsm-policy-bearing <see cref="AgentTurn"/> and runs it through the
/// reusable library agent loop (<see cref="ILlmAgent"/>).
///
/// This is where ALL kgsm authorization policy lives:
///  - which tools are offered (read-only for everyone, authorized-read + commands only for authorized callers);
///  - the per-message blast-radius cap on staged commands;
///  - the defense-in-depth refusal of a command (or authorized read) from an unauthorized caller;
///  - draining the commands the dispatcher staged this turn so the caller can confirm them.
/// The library loop knows none of this — it just evaluates the gate we supply.
/// </summary>
public class ServerAssistant : IServerAssistant
{
    /// <summary>
    /// Blast-radius limit: at most this many commands may be staged (proposed) per user
    /// message. Every command is propose-only (§3.5) and needs a per-op human
    /// confirmation, but this still stops one prompt from teeing up a fleet-wide shuffle
    /// of confirmation buttons. Tunable; kept small on purpose.
    /// </summary>
    private const int MaxStagedCommandsPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on web searches. Each search spends a provider credit and adds an
    /// agent-loop iteration, so this stops one prompt from spraying searches (a runaway loop or
    /// an over-eager refine). The per-day wallet cap is a separate, host-side backstop; this is
    /// the in-turn guard. Tunable; kept small on purpose.
    /// </summary>
    private const int MaxWebSearchesPerMessage = 3;

    private readonly ILlmAgent _agent;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IConfirmationContext _confirmations;
    private readonly IServerInventory _inventory;
    private readonly IServerOperations _operations;
    private readonly IToolRelevanceFilter _toolFilter;
    private readonly IPromptOverrides _promptOverrides;
    private readonly ILogger<ServerAssistant> _logger;

    public ServerAssistant(
        ILlmAgent agent,
        ISystemPromptBuilder promptBuilder,
        IConfirmationContext confirmations,
        IServerInventory inventory,
        IServerOperations operations,
        IToolRelevanceFilter toolFilter,
        IPromptOverrides promptOverrides,
        ILogger<ServerAssistant> logger)
    {
        _agent = agent;
        _promptBuilder = promptBuilder;
        _confirmations = confirmations;
        _inventory = inventory;
        _operations = operations;
        _toolFilter = toolFilter;
        _promptOverrides = promptOverrides;
        _logger = logger;
    }

    /// <summary>
    /// Two-axis tool selection (§3.2): authorization picks the set the caller MAY
    /// use (read-only vs all), then client-requested subset may narrow it further,
    /// then the relevance seam may narrow it (no-op today).
    ///
    /// When <paramref name="requestedTools"/> is provided, every requested name is
    /// validated against the server's own catalog. Unknown names cause a hard error;
    /// names the caller isn't authorized for are silently removed (no information
    /// disclosure). If all requested names are unknown, the full authorized set is
    /// NOT returned — the error propagates to the caller.
    /// </summary>
    private Result<IReadOnlyList<LlmToolDefinition>> SelectTools(
        string userPrompt, bool canPerformActions, IReadOnlyList<string>? requestedTools)
    {
        var authorized = canPerformActions ? LlmTools.All : LlmTools.ReadOnly;

        if (requestedTools is { Count: > 0 })
        {
            // Build the valid-name lookup from the SERVER's catalog — the trust boundary.
            var validTools = authorized.Select(t => t.Tool).ToHashSet();

            // Convert client strings to Tool instances, validating each.
            var requestedToolInstances = new List<Tool>();
            var invalidNames = new List<string>();

            foreach (var name in requestedTools)
            {
                var tool = new Tool(name);
                if (validTools.Contains(tool))
                    requestedToolInstances.Add(tool);
                else
                    invalidNames.Add(name);
            }

            // Hard reject if any requested name is not in the server catalog.
            if (invalidNames.Count > 0)
                return Result.Failure<IReadOnlyList<LlmToolDefinition>>(
                    $"Invalid tool(s): {string.Join(", ", invalidNames)}. " +
                    $"Valid tools: {string.Join(", ", validTools.Select(t => t.Name))}.");

            // Intersect: keep only requested tools that are in the authorized set.
            // Silently removes unauthorized tools (no information disclosure).
            authorized = authorized
                .Where(t => requestedToolInstances.Contains(t.Tool))
                .ToArray();
        }

        var selected = _toolFilter.GetToolsFor(new ToolSelectionContext(userPrompt, canPerformActions), authorized);
        // Apply hot-editable description overrides last (names stay structural; prose is tunable).
        return Result.Success(_promptOverrides.OverlayTools(selected));
    }

    public async Task<AssistantResult> RunAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default)
    {
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools);
        if (toolResult.IsFailure)
            return AssistantResult.Fail(toolResult.Error!);

        var prompt = await _promptBuilder.BuildAsync(canPerformActions, cancellationToken);
        var tools = toolResult.Value!;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = prompt.Text,
            SystemPromptHash = prompt.TemplateHash,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
            Think = think,
        };

        // The dispatcher stages any proposed commands into this per-turn scope; we drain
        // them after the run so the caller can post confirmation prompts.
        using var scope = _confirmations.BeginTurn();
        var result = await _agent.RunAsync(turn, cancellationToken);
        var confirmations = scope.Staged;

        return result.IsSuccess
            ? AssistantResult.Ok(result.Value!.Text, confirmations, result.Value!.Usage)
            : AssistantResult.Fail(result.Error!);
    }

    public async IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        IReadOnlyList<string>? requestedTools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools);
        if (toolResult.IsFailure)
        {
            yield return AssistantStreamEvent.Error(toolResult.Error!);
            yield break;
        }

        var prompt = await _promptBuilder.BuildAsync(canPerformActions, cancellationToken);
        var tools = toolResult.Value!;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = prompt.Text,
            SystemPromptHash = prompt.TemplateHash,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
            Think = think,
        };

        // CRUCIAL: the dispatcher stages destructive ops into an AsyncLocal confirmation scope
        // DURING the agent run, and that ambient value does NOT survive the `yield return`s an
        // async iterator hands to its consumer. So we run the whole turn on a single, yield-free
        // async flow (Produce — structurally identical to the buffered RunAsync, which is why its
        // scope read is reliable) and ferry events out through a channel. This iterator only
        // relays the channel; its own yields never touch the confirmation scope.
        var channel = Channel.CreateUnbounded<AssistantStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var producer = ProduceStreamAsync(turn, channel.Writer, cancellationToken);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken))
                yield return ev;
        }
        finally
        {
            // Observe the producer (surfaces cancellation/faults; it shares the same token, so a
            // cancelled consumer cancels it too). No deadlock: the channel is unbounded.
            await producer;
        }
    }

    /// <summary>
    /// Runs the full agent turn yield-free and writes the mapped events to <paramref name="writer"/>,
    /// draining the staged confirmations after the loop. Being a normal async method (no
    /// <c>yield</c>s to a consumer) keeps the AsyncLocal confirmation scope intact for every
    /// staging the dispatcher does mid-run — the same property the buffered <see cref="RunAsync"/>
    /// relies on.
    /// </summary>
    private async Task ProduceStreamAsync(
        AgentTurn turn,
        ChannelWriter<AssistantStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _confirmations.BeginTurn();
            var finalText = string.Empty;
            LlmUsage? finalUsage = null;
            var errored = false;

            await foreach (var ev in _agent.RunStreamAsync(turn, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                switch (ev.Kind)
                {
                    case AgentEventKind.Token:
                        await writer.WriteAsync(AssistantStreamEvent.Token(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.Thinking:
                        await writer.WriteAsync(AssistantStreamEvent.Thinking(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.ToolStart:
                        if (ev.ToolName is not null)
                            await writer.WriteAsync(
                                AssistantStreamEvent.ToolStart(
                                    ev.ToolName,
                                    ev.ToolArguments ?? new Dictionary<string, string?>()),
                                cancellationToken);
                        break;
                    case AgentEventKind.ToolResult:
                        if (ev.ToolName is not null)
                            await writer.WriteAsync(
                                AssistantStreamEvent.ToolResult(ev.ToolName, ev.ToolSummary ?? string.Empty),
                                cancellationToken);
                        break;
                    case AgentEventKind.Final:
                        finalText = ev.Text ?? string.Empty;
                        finalUsage = ev.Usage;
                        break;
                    case AgentEventKind.Error:
                        // Terminal: a failed turn stages nothing to confirm.
                        await writer.WriteAsync(
                            AssistantStreamEvent.Error(ev.ErrorMessage ?? "The assistant failed."), cancellationToken);
                        errored = true;
                        break;
                }

                if (errored)
                    break;
            }

            if (!errored)
            {
                // The scope's list is intact (no consumer yields disturbed this flow) — drain it.
                foreach (var confirmation in scope.Staged)
                    await writer.WriteAsync(AssistantStreamEvent.Confirmation(confirmation), cancellationToken);

                await writer.WriteAsync(AssistantStreamEvent.Final(finalText, finalUsage), cancellationToken);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    public async Task<Result<string>> ConfirmAsync(
        PendingConfirmation confirmation,
        bool canPerformActions,
        CancellationToken cancellationToken = default)
    {
        // Authority is checked fresh here — never trusted from the staged operation or
        // any token that carried it. Defense in depth alongside the host's own check.
        if (!canPerformActions)
            return Result.Failure<string>("You don't have permission to perform server actions.");

        return confirmation.Kind switch
        {
            ConfirmationKind.Uninstall => await ConfirmUninstallAsync(confirmation.Target, cancellationToken),
            ConfirmationKind.Install => await ConfirmInstallAsync(
                confirmation.Target, confirmation.InstanceName, cancellationToken),
            ConfirmationKind.SetConfig => await ConfirmSetConfigAsync(
                confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue, cancellationToken),
            ConfirmationKind.Start or ConfirmationKind.Stop or ConfirmationKind.Restart
                or ConfirmationKind.Update or ConfirmationKind.Backup
                => await ConfirmCommandAsync(confirmation.Kind, confirmation.Target, cancellationToken),
            _ => Result.Failure<string>("Unknown action; nothing was done."),
        };
    }

    /// <summary>
    /// Executes a confirmed single-instance command (start/stop/restart/update/backup).
    /// Re-validates the target still exists (it was resolved at staging time, which may
    /// have been a while ago, and a stateless token is replayable within its lifetime),
    /// then runs the matching <see cref="IServerOperations"/> op.
    /// </summary>
    private async Task<Result<string>> ConfirmCommandAsync(
        ConfirmationKind kind, string target, CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>(
                $"'{target}' no longer exists — nothing to {ConfirmationKinds.Verb(kind)}.");

        Func<string, CancellationToken, Task<Result>> op = kind switch
        {
            ConfirmationKind.Start => _operations.StartAsync,
            ConfirmationKind.Stop => _operations.StopAsync,
            ConfirmationKind.Restart => _operations.RestartAsync,
            ConfirmationKind.Update => _operations.UpdateAsync,
            ConfirmationKind.Backup => _operations.CreateBackupAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a single-instance command"),
        };

        _logger.LogInformation("Confirmed {Verb} of {Instance}", ConfirmationKinds.Verb(kind), match);

        var result = await op(match, cancellationToken);
        return result.IsSuccess
            ? Result.Success($"{match} has been {ConfirmationKinds.PastTense(kind)}.")
            : Result.Failure<string>(
                $"Could not {ConfirmationKinds.Verb(kind)} '{match}': {result.Error ?? "unknown error"}.");
    }

    /// <summary>
    /// Re-validates the target still exists (it was resolved at staging time, which may
    /// have been a while ago, and a stateless token is replayable within its lifetime),
    /// then uninstalls it.
    /// </summary>
    private async Task<Result<string>> ConfirmUninstallAsync(string target, CancellationToken cancellationToken)
    {
        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>($"'{target}' no longer exists — nothing to uninstall.");

        _logger.LogInformation("Confirmed uninstall of {Instance}", match);

        var result = await _operations.UninstallAsync(match, cancellationToken);
        return result.IsSuccess
            ? Result.Success($"Uninstalled '{match}'.")
            : Result.Failure<string>($"Could not uninstall '{match}': {result.Error ?? "unknown error"}.");
    }

    /// <summary>
    /// Re-validates the blueprint still exists and the requested name doesn't now
    /// collide (replay/race-safe), then installs.
    /// </summary>
    private async Task<Result<string>> ConfirmInstallAsync(
        string blueprint, string? instanceName, CancellationToken cancellationToken)
    {
        var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
        var match = blueprints.FirstOrDefault(
            k => string.Equals(k, blueprint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>($"Blueprint '{blueprint}' is no longer available.");

        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            if (instances.Keys.Any(k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase)))
                return Result.Failure<string>(
                    $"An instance named '{instanceName}' already exists — pick another name.");
        }
        else
        {
            instanceName = null;
        }

        _logger.LogInformation("Confirmed install of {Blueprint} (name={Name})", match, instanceName ?? "(default)");

        var result = await _operations.InstallAsync(match, instanceName, cancellationToken);
        var named = instanceName is null ? "" : $" (named '{instanceName}')";
        return result.IsSuccess
            ? Result.Success($"Installed a new '{match}' server{named}.")
            : Result.Failure<string>($"Could not install '{match}': {result.Error ?? "unknown error"}.");
    }

    /// <summary>
    /// Re-validates the instance still exists (it was resolved at staging time, and a
    /// stateless token is replayable within its lifetime), then sets one config value.
    /// kgsm owns the key-safety policy, so a refused (denylisted/invalid) key surfaces
    /// here as a failed <see cref="Result"/> reported to the user, never an exception.
    /// </summary>
    private async Task<Result<string>> ConfirmSetConfigAsync(
        string target, string? key, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure<string>("No configuration key was given — nothing to set.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>($"'{target}' no longer exists — nothing to configure.");

        // The value may legitimately be the empty string (clearing the setting).
        var newValue = value ?? string.Empty;

        _logger.LogInformation("Confirmed set-config of {Instance} ({Key})", match, key);

        var result = await _operations.SetInstanceConfigValueAsync(match, key, newValue, cancellationToken);
        var shown = newValue.Length == 0 ? "(empty)" : newValue;
        return result.IsSuccess
            ? Result.Success($"Set {key} = {shown} on '{match}'.")
            : Result.Failure<string>($"Could not set {key} on '{match}': {result.Error ?? "unknown error"}.");
    }

    /// <summary>
    /// Per-turn gate closure.
    ///  - Read-only tools always pass.
    ///  - Authorized reads (e.g. view_config_file): refused for unauthorized callers, but not capped.
    ///  - Commands (start/stop/restart/update/backup/install/uninstall): refused for unauthorized
    ///    callers; otherwise allowed through to the dispatcher, which only STAGES them (it never
    ///    executes). Every command is propose-only (§3.5), so there is one cap — the count of ops
    ///    proposed this message — at <see cref="MaxStagedCommandsPerMessage"/>.
    /// The closure holds the per-message staging counter.
    /// </summary>
    private static Func<LlmToolCall, ToolGate> BuildGate(bool canPerformActions)
    {
        var staged = 0;
        var searches = 0;
        return call =>
        {
            // web_search is read-only (open to everyone), but each call spends a credit, so it
            // carries its own per-message cap — the wallet's in-turn guard (the per-day ceiling
            // lives host-side). Checked before the read-only pass-through below.
            if (call.Name == LlmTools.WebSearch)
            {
                if (searches >= MaxWebSearchesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxWebSearchesPerMessage} web searches per message. " +
                        "Answer from what you already found, or tell the user you couldn't find it.");

                searches++;
                return ToolGate.Allow;
            }

            if (LlmTools.IsAuthorizedRead(call.Name))
                return canPerformActions
                    ? ToolGate.Allow
                    : ToolGate.Refuse("Refused: you don't have permission to view server configuration.");

            if (!LlmTools.IsStagedCommand(call.Name))
                return ToolGate.Allow; // read-only

            if (!canPerformActions)
                return ToolGate.Refuse("Refused: you don't have permission to perform server actions.");

            if (staged >= MaxStagedCommandsPerMessage)
                return ToolGate.Refuse(
                    $"Refused: at most {MaxStagedCommandsPerMessage} server actions can be proposed " +
                    "per message. Ask the user to do the rest separately.");

            staged++;
            return ToolGate.Allow;
        };
    }
}
