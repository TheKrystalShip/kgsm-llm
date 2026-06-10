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
///  - which tools are offered (read-only for everyone, mutating/destructive only for authorized callers);
///  - the per-message blast-radius cap on mutating actions;
///  - the defense-in-depth refusal of a mutating/destructive call from an unauthorized caller;
///  - draining the destructive ops the dispatcher staged this turn so the caller can confirm them.
/// The library loop knows none of this — it just evaluates the gate we supply.
/// </summary>
public class ServerAssistant : IServerAssistant
{
    /// <summary>Blast-radius limit: at most this many mutating actions per user message.</summary>
    private const int MaxActionsPerMessage = 5;

    /// <summary>
    /// Blast-radius limit on the DESTRUCTIVE tier: at most this many install/uninstall
    /// ops may be staged (proposed) per user message. These never execute without a
    /// per-op human confirmation, but this still stops one prompt from teeing up a
    /// library-wide shuffle. Tunable; kept small on purpose.
    /// </summary>
    private const int MaxDestructiveStagedPerMessage = 3;

    private readonly ILlmAgent _agent;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IConfirmationContext _confirmations;
    private readonly IServerInventory _inventory;
    private readonly IServerOperations _operations;
    private readonly ILogger<ServerAssistant> _logger;

    public ServerAssistant(
        ILlmAgent agent,
        ISystemPromptBuilder promptBuilder,
        IConfirmationContext confirmations,
        IServerInventory inventory,
        IServerOperations operations,
        ILogger<ServerAssistant> logger)
    {
        _agent = agent;
        _promptBuilder = promptBuilder;
        _confirmations = confirmations;
        _inventory = inventory;
        _operations = operations;
        _logger = logger;
    }

    public async Task<AssistantResult> RunAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptBuilder.BuildAsync(canPerformActions, cancellationToken);

        // Mutating + destructive tools are only offered to authorized callers; the gate re-checks.
        var tools = canPerformActions ? LlmTools.All : LlmTools.ReadOnly;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = systemPrompt,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
        };

        // The dispatcher stages any destructive ops into this per-turn scope; we drain
        // them after the run so the caller can post confirmation prompts.
        using var scope = _confirmations.BeginTurn();
        var result = await _agent.RunAsync(turn, cancellationToken);
        var confirmations = scope.Staged;

        return result.IsSuccess
            ? AssistantResult.Ok(result.Value!, confirmations)
            : AssistantResult.Fail(result.Error!);
    }

    public async IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptBuilder.BuildAsync(canPerformActions, cancellationToken);

        // Identical policy to RunAsync: mutating/destructive tools only for authorized callers;
        // the gate re-checks each call and enforces the per-message blast caps.
        var tools = canPerformActions ? LlmTools.All : LlmTools.ReadOnly;

        var turn = new AgentTurn
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            SystemPrompt = systemPrompt,
            Tools = tools,
            Gate = BuildGate(canPerformActions),
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
            var errored = false;

            await foreach (var ev in _agent.RunStreamAsync(turn, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                switch (ev.Kind)
                {
                    case AgentEventKind.Token:
                        await writer.WriteAsync(AssistantStreamEvent.Token(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.Status:
                        await writer.WriteAsync(AssistantStreamEvent.Status(ev.Text ?? string.Empty), cancellationToken);
                        break;
                    case AgentEventKind.Final:
                        finalText = ev.Text ?? string.Empty;
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

                await writer.WriteAsync(AssistantStreamEvent.Final(finalText), cancellationToken);
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
            _ => Result.Failure<string>("Unknown action; nothing was done."),
        };
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
    /// Per-turn gate closure.
    ///  - Read-only tools always pass.
    ///  - Mutating tools: refused for unauthorized callers; capped at <see cref="MaxActionsPerMessage"/>.
    ///  - Destructive tools: refused for unauthorized callers; otherwise allowed through to the
    ///    dispatcher, which only STAGES them (it never executes), so they don't consume the cap.
    /// The closure holds the per-message mutating-action counter.
    /// </summary>
    private static Func<LlmToolCall, ToolGate> BuildGate(bool canPerformActions)
    {
        var actionsTaken = 0;
        var destructiveStaged = 0;
        return call =>
        {
            if (LlmTools.IsDestructive(call.Name))
            {
                if (!canPerformActions)
                    return ToolGate.Refuse("Refused: you don't have permission to perform server actions.");

                if (destructiveStaged >= MaxDestructiveStagedPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxDestructiveStagedPerMessage} install/uninstall actions " +
                        "can be proposed per message. Ask the user to do these one at a time.");

                destructiveStaged++;
                return ToolGate.Allow;
            }

            if (!LlmTools.IsMutating(call.Name))
                return ToolGate.Allow;

            if (!canPerformActions)
                return ToolGate.Refuse(
                    "Refused: you don't have permission to perform server actions.");

            if (actionsTaken >= MaxActionsPerMessage)
                return ToolGate.Refuse(
                    $"Refused: the limit of {MaxActionsPerMessage} actions per message has been " +
                    "reached. Ask the user to send the remaining actions separately.");

            actionsTaken++;
            return ToolGate.Allow;
        };
    }
}
