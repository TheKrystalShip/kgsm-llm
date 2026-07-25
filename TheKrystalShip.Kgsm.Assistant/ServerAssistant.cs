using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Network;
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
    /// Per-message ceiling on <c>search</c> calls. Each adds an agent-loop iteration (and a web
    /// fallback may spend a provider credit), so this stops one prompt from spraying lookups (a
    /// runaway loop or an over-eager refine). It is now a loop-runaway guard, not the wallet guard —
    /// the per-day web spend cap lives host-side in the provider. Matches the staging cap; tunable.
    /// </summary>
    private const int MaxSearchesPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on <c>fetch_url</c> calls — mirrors <see cref="MaxSearchesPerMessage"/> for
    /// the same reason (a loop-runaway guard; the per-day fetch wallet cap lives host-side in the
    /// adapter). Each fetch is a real outbound HTTP request against a model/user-influenced URL, so
    /// this stops one prompt from spraying page reads.
    /// </summary>
    private const int MaxFetchesPerMessage = 5;

    /// <summary>
    /// Per-message ceiling on <c>create_blueprint</c> calls. Kept at 1 — each run is a real research +
    /// test-install + teardown pipeline (far heavier than a search or fetch), so one message proposing
    /// several new games at once is refused rather than fanning out several probes concurrently.
    /// </summary>
    private const int MaxBlueprintAuthoringsPerMessage = 1;

    private readonly ILlmAgent _agent;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IConfirmationContext _confirmations;
    private readonly ITurnProgress _progress;
    private readonly IServerInventory _inventory;
    private readonly IServerOperations _operations;
    private readonly INetworkInfo _network;
    private readonly IUpnpInfo _upnp;
    private readonly IToolRelevanceFilter _toolFilter;
    private readonly IPromptOverrides _promptOverrides;
    private readonly IBlueprintAuthoring _blueprintAuthoring;
    private readonly SearchOptions _searchOptions;
    private readonly FetchOptions _fetchOptions;
    private readonly BlueprintAuthoringFlags _blueprintAuthoringFlags;
    private readonly ILogger<ServerAssistant> _logger;

    public ServerAssistant(
        ILlmAgent agent,
        ISystemPromptBuilder promptBuilder,
        IConfirmationContext confirmations,
        ITurnProgress progress,
        IServerInventory inventory,
        IServerOperations operations,
        INetworkInfo network,
        IUpnpInfo upnp,
        IToolRelevanceFilter toolFilter,
        IPromptOverrides promptOverrides,
        IBlueprintAuthoring blueprintAuthoring,
        IOptions<SearchOptions> searchOptions,
        IOptions<FetchOptions> fetchOptions,
        IOptions<BlueprintAuthoringFlags> blueprintAuthoringFlags,
        ILogger<ServerAssistant> logger)
    {
        _agent = agent;
        _promptBuilder = promptBuilder;
        _confirmations = confirmations;
        _progress = progress;
        _inventory = inventory;
        _operations = operations;
        _network = network;
        _upnp = upnp;
        _toolFilter = toolFilter;
        _promptOverrides = promptOverrides;
        _blueprintAuthoring = blueprintAuthoring;
        _searchOptions = searchOptions.Value;
        _fetchOptions = fetchOptions.Value;
        _blueprintAuthoringFlags = blueprintAuthoringFlags.Value;
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

        // §D7 omit-when-disabled: the unified `search` tool is offered only when at least one source
        // backs it (RAG enabled and/or a web provider configured). Removed BEFORE the requested-tool
        // validation below, so a client asking for `search` on a host where it's unavailable gets the
        // honest invalid-tool error — never a dead tool the model would call and watch fail.
        if (!_searchOptions.Available)
            authorized = authorized.Where(t => t.Tool != LlmTools.Search).ToArray();

        // Same §D7 omit-when-disabled rule for fetch_url: offered only when a real IWebFetch adapter
        // is enabled on this host (FetchOptions.Available).
        if (!_fetchOptions.Available)
            authorized = authorized.Where(t => t.Tool != LlmTools.FetchUrl).ToArray();

        // Same §D7 omit-when-disabled rule for create_blueprint: offered only when the real authoring
        // pipeline is enabled on this host (BlueprintAuthoringFlags.Available, false everywhere by default).
        if (!_blueprintAuthoringFlags.Available)
            authorized = authorized.Where(t => t.Tool != LlmTools.CreateBlueprint).ToArray();

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
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default)
    {
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools);
        if (toolResult.IsFailure)
            return AssistantResult.Fail(toolResult.Error!);

        var prompt = await _promptBuilder.BuildAsync(canPerformActions, autoExecute, cancellationToken);
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
        // them after the run so the caller can post confirmation prompts. On an auto-accept
        // turn the dispatcher runs lifecycle verbs immediately instead (scope reads back AutoExecute).
        using var scope = _confirmations.BeginTurn(autoExecute);
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
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolResult = SelectTools(userPrompt, canPerformActions, requestedTools);
        if (toolResult.IsFailure)
        {
            yield return AssistantStreamEvent.Error(toolResult.Error!);
            yield break;
        }

        var prompt = await _promptBuilder.BuildAsync(canPerformActions, autoExecute, cancellationToken);
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
        // SingleWriter is false: a long-running tool (create_blueprint) reports progress steps via
        // ITurnProgress from DEEP inside its own execution — a call site nested under the producer's
        // `await DispatchRoundAsync` in the generic agent loop, on whatever thread the dispatch task
        // resumes on. Those writes and the producer's own mapped-event writes are no longer provably
        // the same writer, so the single-writer fast path is unsafe; System.Threading.Channels supports
        // multiple concurrent writers on an unbounded channel without any extra synchronisation here.
        // Progress writes still happen-before the tool's own `tool.result` write (the aggregator
        // reports and returns before the dispatcher's ExecuteAsync call completes), so ordering holds.
        var channel = Channel.CreateUnbounded<AssistantStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var producer = ProduceStreamAsync(turn, autoExecute, channel.Writer, cancellationToken);
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
        bool autoExecute,
        ChannelWriter<AssistantStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _confirmations.BeginTurn(autoExecute);
            // Opens the SAME per-turn ambient pattern as the confirmation scope above, carrying the
            // SAME writer this method already relays mapped agent events into — a long tool reports a
            // step by writing straight onto it (see ITurnProgress), landing on the stream immediately
            // instead of waiting for its own terminal tool.result.
            using var progressScope = _progress.BeginTurn(writer);
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
                                    ev.ToolArguments ?? new Dictionary<string, string?>(),
                                    ev.ToolCallId),
                                cancellationToken);
                        break;
                    case AgentEventKind.ToolResult:
                        if (ev.ToolName is not null)
                            await writer.WriteAsync(
                                // Forward the opaque card (ev.ToolData) through verbatim — the domain layer
                                // produced it (ToolDispatcher) and the Service serialises it; we just relay.
                                AssistantStreamEvent.ToolResult(
                                    ev.ToolName, ev.ToolSummary ?? string.Empty, ev.ToolCallId, ev.ToolData),
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
            ConfirmationKind.OpenPorts => await ConfirmOpenPortsAsync(
                confirmation.Target, confirmation.ConfigValue, confirmation.ConfigKey, cancellationToken),
            ConfirmationKind.WriteFile => await ConfirmWriteFileAsync(
                confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue, cancellationToken),
            // The text path (CLI, and any surface that only reads ConfirmResponse.Text): finalize returns a
            // rich card, but here we surface only its summary line. A card surface (the Service) calls
            // FinalizeBlueprintAsync directly to render the DraftReady/Verified card + any re-edit token.
            ConfirmationKind.Blueprint => Result.Success((await FinalizeBlueprintAsync(
                confirmation.InstanceName ?? confirmation.Target, confirmation.ConfigValue ?? string.Empty,
                canPerformActions, cancellationToken)).Summary),
            ConfirmationKind.Start or ConfirmationKind.Stop or ConfirmationKind.Restart
                or ConfirmationKind.Update or ConfirmationKind.Backup
                => await ConfirmCommandAsync(confirmation.Kind, confirmation.Target, cancellationToken),
            _ => Result.Failure<string>("Unknown action; nothing was done."),
        };
    }

    /// <summary>
    /// Finalizes an assistant-authored blueprint after the user reviewed/edited it in the chat: re-validates
    /// the (possibly edited) YAML and runs the test-install → verify → repair → keep/stash pipeline. This is
    /// the blueprint counterpart of <see cref="ConfirmAsync"/> — it exists separately because its result is a
    /// rich <see cref="BlueprintAuthoringData"/> card (a Verified card, or a fresh DraftReady card for
    /// another edit when the repair loop exhausts), not the one-line text a normal confirm returns. Authority
    /// is checked fresh here, exactly as in <see cref="ConfirmAsync"/>.
    /// </summary>
    public async Task<ToolResult<BlueprintAuthoringData>> FinalizeBlueprintAsync(
        string game, string editedYaml, bool canPerformActions, CancellationToken cancellationToken = default)
    {
        if (!canPerformActions)
        {
            var denied = "You don't have permission to add a blueprint to the catalog.";
            return new ToolResult<BlueprintAuthoringData>(
                LlmTools.CreateBlueprint, Confidence.Likely, new ResultRef(ResourceKind.Blueprint, game), denied,
                new BlueprintAuthoringData(BlueprintAuthoringOutcome.Failed, game, null, [], null, denied, false));
        }

        _logger.LogInformation("Confirmed blueprint finalize for \"{Game}\"", game);
        return await _blueprintAuthoring.FinalizeAsync(game, editedYaml, cancellationToken);
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
    /// Re-validates the instance still exists (it was resolved at staging time, and a stateless token is
    /// replayable within its lifetime), then overwrites the file at the staged path (<paramref name="key"/>)
    /// with the staged content (<paramref name="value"/>) via <see cref="IServerOperations.WriteInstanceFileAsync"/>
    /// — the same jail the read tools use. The Service rehydrates the real content from its pending-write
    /// store BEFORE calling this (see the /confirm handler); this method always sees real content and stays
    /// store-agnostic. A jail violation, size-cap refusal, or I/O failure surfaces as a failed
    /// <see cref="Result"/>, never an exception.
    /// </summary>
    private async Task<Result<string>> ConfirmWriteFileAsync(
        string target, string? key, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure<string>("No file path was given — nothing was written.");
        if (string.IsNullOrEmpty(value))
            return Result.Failure<string>("No content was given — nothing was written.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>($"'{target}' no longer exists — nothing to write.");

        _logger.LogInformation("Confirmed write-file of {Instance} ({Path})", match, key);

        var result = await _operations.WriteInstanceFileAsync(match, key, value, cancellationToken);
        return result.IsSuccess
            ? Result.Success($"Wrote '{key}' on '{match}'. A running server picks up the change on its next restart.")
            : Result.Failure<string>($"Could not write '{key}' on '{match}': {result.Error ?? "unknown error"}.");
    }

    /// <summary>
    /// Re-validates the instance still exists (it was resolved at staging time, and a stateless token is
    /// replayable within its lifetime), re-parses the port spec carried on the token, then opens the
    /// HOST-FIREWALL ports via <see cref="INetworkInfo.OpenPortsAsync"/> — the real execution point.
    /// The firewall authority's precise outcome is mapped to an honest user-facing result: an unreachable
    /// authority, an unsupported backend, and an inactive (staged-not-enforcing) backend each read
    /// differently and never as a fabricated "opened".
    /// <para>
    /// When the router leg was opted in at staging (<paramref name="scope"/> == <c>"router"</c>), it ALSO
    /// opens the router / UPnP forward for the same ports via <see cref="IUpnpInfo.OpenForwardsAsync"/> (a
    /// separate authority — the watchdog) and appends an honest per-axis clause: applied, skipped (the
    /// instance has port-forwarding disabled), failed, or watchdog-unavailable — never a fabricated forward.
    /// The firewall outcome still determines success/failure; the router clause is additive context.
    /// </para>
    /// </summary>
    private async Task<Result<string>> ConfirmOpenPortsAsync(
        string target, string? portSpec, string? scope, CancellationToken cancellationToken)
    {
        if (!PortSpecParser.TryParse(portSpec, out var ports, out var parseError))
            return Result.Failure<string>($"The staged ports were invalid ({parseError}) — nothing was opened.");

        var instances = await _inventory.GetInstancesAsync(cancellationToken);
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result.Failure<string>($"'{target}' no longer exists — nothing to open ports on.");

        bool includeRouter = string.Equals(scope, "router", StringComparison.Ordinal);
        _logger.LogInformation("Confirmed open-ports of {Instance} ({Ports}, router={Router})",
            match, PortSpecParser.ToCanonical(ports), includeRouter);

        var result = await _network.OpenPortsAsync(match, ports, cancellationToken);
        var shown = PortSpecParser.ToDisplay(ports);
        var backend = string.IsNullOrWhiteSpace(result.Backend) ? "the firewall" : result.Backend;

        var firewall = result.State switch
        {
            NetworkActionState.Applied =>
                Result.Success($"Opened host-firewall port(s) {shown} for '{match}' ({backend})."),
            NetworkActionState.AppliedInactive =>
                Result.Success($"Wrote host-firewall rules for port(s) {shown} on '{match}', but {backend} isn't " +
                               "enforcing yet — they'll take effect when it's enabled (the ports are reachable meanwhile)."),
            NetworkActionState.NoOp =>
                Result.Success($"Host-firewall port(s) {shown} were already open for '{match}' — nothing to change."),
            NetworkActionState.Unsupported =>
                Result.Failure<string>($"This host has no firewall backend that can open ports, so nothing was changed for '{match}'."),
            NetworkActionState.FirewallUnavailable =>
                Result.Failure<string>($"The firewall service isn't reachable, so the port(s) for '{match}' couldn't be opened. Nothing was changed."),
            _ =>
                Result.Failure<string>($"Could not open host-firewall port(s) {shown} for '{match}': {result.Detail ?? "the firewall rejected the change"}."),
        };

        if (!includeRouter)
            return firewall;

        // The router leg — a separate authority (the watchdog). Its outcome is appended honestly and never
        // upgrades a firewall failure to success (the firewall Result carries success/failure).
        var upnp = await _upnp.OpenForwardsAsync(match, ports, cancellationToken);
        string routerClause = upnp.State switch
        {
            UpnpActionState.Applied => $"Also opened the router/UPnP forward for {shown}.",
            UpnpActionState.Skipped => "The router/UPnP forward was skipped — this server has port-forwarding disabled, so nothing was forwarded on the router.",
            UpnpActionState.Unavailable => "The router/UPnP forward couldn't be set — the watchdog isn't reachable.",
            _ => $"The router/UPnP forward failed{(string.IsNullOrWhiteSpace(upnp.Detail) ? "" : $" ({upnp.Detail})")} — the host firewall change above still stands.",
        };

        string combined = $"{firewall.Value ?? firewall.Error} {routerClause}";
        // Preserve the firewall axis's success/failure; append the router context either way.
        return firewall.IsSuccess ? Result.Success(combined) : Result.Failure<string>(combined);
    }

    /// <summary>
    /// Per-turn gate closure.
    ///  - Read-only tools always pass.
    ///  - Authorized reads (read_file / list_files): refused for unauthorized callers, but not capped.
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
        var fetches = 0;
        var authored = 0;
        return call =>
        {
            // create_blueprint is authorized-and-autonomous (LlmTools.AuthorizedActions): refused for an
            // unauthorized caller exactly like a staged command (defense in depth behind SelectTools
            // already omitting it for those callers), but it never stages — it runs to completion inline,
            // so its own per-message cap replaces the staging cap below.
            if (call.Name == LlmTools.CreateBlueprint)
            {
                if (!canPerformActions)
                    return ToolGate.Refuse("Refused: you don't have permission to perform server actions.");

                if (authored >= MaxBlueprintAuthoringsPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxBlueprintAuthoringsPerMessage} new blueprint(s) can be " +
                        "authored per message. Ask the user to try the rest separately.");

                authored++;
                return ToolGate.Allow;
            }


            // search is read-only (open to everyone), but each call adds a loop iteration (and a web
            // fallback may spend a credit), so it carries its own per-message cap — the in-turn
            // runaway guard (the per-day web wallet ceiling lives host-side). Checked before the
            // read-only pass-through below.
            if (call.Name == LlmTools.Search)
            {
                if (searches >= MaxSearchesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxSearchesPerMessage} searches per message. " +
                        "Answer from what you already found, or tell the user you couldn't find it.");

                searches++;
                return ToolGate.Allow;
            }

            // fetch_url is likewise read-only and open to everyone, but each call is a real outbound
            // HTTP request against a model/user-influenced URL and adds a loop iteration — its own
            // per-message cap, independent of the search cap.
            if (call.Name == LlmTools.FetchUrl)
            {
                if (fetches >= MaxFetchesPerMessage)
                    return ToolGate.Refuse(
                        $"Refused: at most {MaxFetchesPerMessage} page fetches per message. " +
                        "Answer from what you already fetched, or tell the user you couldn't fetch it.");

                fetches++;
                return ToolGate.Allow;
            }

            if (LlmTools.IsAuthorizedRead(call.Name))
                return canPerformActions
                    ? ToolGate.Allow
                    : ToolGate.Refuse("Refused: you don't have permission to read server files.");

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
