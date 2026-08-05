using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The outcome of an assistant turn: the reply text to show the user, plus any
/// destructive operations that were staged this turn and now need explicit human
/// confirmation before they run, plus the turn's token <see cref="Usage"/> so a host
/// can show context occupancy (null if the backend reported none).
/// </summary>
public sealed record AssistantResult
{
    public bool IsSuccess { get; private init; }
    public string? Error { get; private init; }
    public string Text { get; private init; } = string.Empty;
    public IReadOnlyList<PendingConfirmation> Confirmations { get; private init; } =
        Array.Empty<PendingConfirmation>();
    public LlmUsage? Usage { get; private init; }

    public bool IsFailure => !IsSuccess;

    public static AssistantResult Ok(
        string text, IReadOnlyList<PendingConfirmation> confirmations, LlmUsage? usage = null) =>
        new() { IsSuccess = true, Text = text, Confirmations = confirmations, Usage = usage };

    public static AssistantResult Fail(string error) =>
        new() { IsSuccess = false, Error = error };
}

/// <summary>The kind of event emitted while streaming an assistant turn.</summary>
public enum AssistantEventKind
{
    /// <summary>An incremental slice of the assistant's reply text.</summary>
    Token,

    /// <summary>An incremental slice of the model's internal reasoning (thinking) content.</summary>
    Thinking,

    /// <summary>A tool is about to be dispatched; carries its name + arguments.</summary>
    ToolStart,

    /// <summary>A tool finished; carries its name, a summary, and an optional structured card (§5·a result).</summary>
    ToolResult,

    /// <summary>
    /// A long-running tool reported one step of its own progress WHILE still executing (e.g.
    /// <c>create_blueprint</c>'s research→draft→install→verify stages). Carries
    /// <see cref="AssistantStreamEvent.ProgressKey"/>/<see cref="AssistantStreamEvent.ProgressLabel"/>;
    /// never terminal, and never a substitute for the tool's own <see cref="ToolResult"/>.
    /// </summary>
    Progress,

    /// <summary>A destructive op staged this turn, now awaiting human confirmation.</summary>
    Confirmation,

    /// <summary>The turn is complete; <see cref="AssistantStreamEvent.Text"/> holds the full reply.</summary>
    Final,

    /// <summary>The turn failed; <see cref="AssistantStreamEvent.ErrorMessage"/> explains why. Terminal.</summary>
    Error
}

/// <summary>
/// A single event from <see cref="IServerAssistant.RunStreamAsync"/>: the streaming analogue of
/// <see cref="AssistantResult"/>. Tokens arrive as they generate; any staged
/// <see cref="Confirmation"/>s surface after the reply and before the terminal
/// <see cref="AssistantEventKind.Final"/> (or <see cref="AssistantEventKind.Error"/>). The event
/// carries the RAW <see cref="PendingConfirmation"/> — the host mints the confirmation token, so
/// the library stays oblivious to the host's token scheme.
/// </summary>
public sealed record AssistantStreamEvent(
    AssistantEventKind Kind,
    string? Text = null,
    PendingConfirmation? StagedConfirmation = null,
    string? ErrorMessage = null,
    Tool? ToolName = null,
    IReadOnlyDictionary<string, string?>? ToolArguments = null,
    string? ToolSummary = null,
    LlmUsage? Usage = null,
    string? ToolCallId = null,
    object? ToolData = null,
    string? ProgressKey = null,
    string? ProgressLabel = null,
    string? ProgressStatus = null,
    long TurnId = 0)
{
    public static AssistantStreamEvent Token(string delta) => new(AssistantEventKind.Token, Text: delta);
    public static AssistantStreamEvent Thinking(string delta) => new(AssistantEventKind.Thinking, Text: delta);
    public static AssistantStreamEvent ToolStart(Tool tool, IReadOnlyDictionary<string, string?> arguments, string? id = null) =>
        new(AssistantEventKind.ToolStart, ToolName: tool, ToolArguments: arguments, ToolCallId: id);
    // `data` is the dispatcher's optional surface-facing card (§5·a tool.result.result), carried opaquely.
    public static AssistantStreamEvent ToolResult(Tool tool, string summary, string? id = null, object? data = null) =>
        new(AssistantEventKind.ToolResult, ToolName: tool, ToolSummary: summary, ToolCallId: id, ToolData: data);
    // No tool-call `id`: unlike ToolStart/ToolResult (minted by the generic agent loop right where it
    // dispatches), a progress step is reported from deep inside the tool's own execution (the aggregator),
    // which is never handed the loop's synthesised id — see ITurnProgress. `status` is always "active"
    // today (a step is reported when it begins); the parameter exists for a future terminal/failed step.
    public static AssistantStreamEvent Progress(Tool tool, string key, string label, string status = "active") =>
        new(AssistantEventKind.Progress, ToolName: tool, ProgressKey: key, ProgressLabel: label, ProgressStatus: status);
    public static AssistantStreamEvent Confirmation(PendingConfirmation confirmation) =>
        new(AssistantEventKind.Confirmation, StagedConfirmation: confirmation);

    /// <summary>
    /// The terminal success event: the full reply, the turn's token usage (if any), and the id the turn
    /// was recorded under so a surface can act on the answer it just received rather than infer which
    /// turn "the last one" was. <c>0</c> when the turn was not persisted and has no durable handle.
    /// </summary>
    public static AssistantStreamEvent Final(string text, LlmUsage? usage = null, long turnId = 0) =>
        new(AssistantEventKind.Final, Text: text, Usage: usage, TurnId: turnId);
    public static AssistantStreamEvent Error(string error) => new(AssistantEventKind.Error, ErrorMessage: error);
}

/// <summary>
/// The kgsm-specific entry point into the LLM agent. Owns the application policy
/// — which tools to offer, the per-message action cap, authorization, and staging
/// destructive ops for confirmation — then hands a fully-formed turn to the
/// reusable library agent loop. Stateful only through the conversation store and
/// the per-turn confirmation scope; safe to share as a singleton.
/// </summary>
public interface IServerAssistant
{
    /// <param name="conversationId">
    /// Opaque key identifying the conversation for memory (e.g. a Discord
    /// "{userId}:{channelId}", or a web session id). The host owns the scheme.
    /// </param>
    /// <param name="userPrompt">The user's message for this turn.</param>
    /// <param name="canPerformActions">
    /// Whether the requesting user is authorized to run mutating/destructive actions.
    /// When false, those tools are neither offered nor executed.
    /// </param>
    /// <param name="think">
    /// Whether the model should stream its reasoning (thinking) for this turn.
    /// </param>
    /// <param name="autoExecute">
    /// Whether this is an auto-accept turn: the host has verified the caller is an admin who turned
    /// the toggle on, so the dispatcher RUNS lifecycle commands (start/stop/restart/update/backup)
    /// immediately instead of staging them. Implies <paramref name="canPerformActions"/>; install /
    /// uninstall / set-config stay propose-only even when true.
    /// </param>
    /// <param name="requestedTools">
    /// Optional tool names the client wants available this turn. When null or empty,
    /// all authorized tools are used. Invalid names cause a hard error; names the
    /// caller isn't authorized for are silently removed.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the turn.</param>
    /// <param name="userDisplay">
    /// Optional display name of the asking user, recorded onto the stored turn so the conversation log
    /// carries a human-readable name beside the opaque <paramref name="conversationId"/>. The host owns
    /// it (only the host knows who the id belongs to); null simply records none. Last param to keep
    /// existing positional callers unshifted.
    /// </param>
    Task<AssistantResult> RunAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default,
        // The current content of a blueprint draft the user is reviewing in the editor, when this turn
        // carries one (the SPA sends it). Present ⇒ revise_blueprint is offered and the draft is injected
        // into this turn's context so the model can change it. Last param (after ct) to keep every existing
        // positional caller/mock unshifted.
        string? openDraftYaml = null,
        string? userDisplay = null);

    /// <summary>
    /// Streaming counterpart to <see cref="RunAsync"/>: same policy (tool offering, gate, blast
    /// caps, confirmation staging) but yields <see cref="AssistantStreamEvent"/>s as the turn
    /// unfolds — reply <c>Token</c>s, tool-round <c>Status</c>, then any staged
    /// <c>Confirmation</c>s, ending with one terminal <c>Final</c> or <c>Error</c>. Authority is
    /// still passed in (the host derives it from the verified principal), never inferred here.
    /// On an auto-accept turn (<paramref name="autoExecute"/>) lifecycle commands run immediately,
    /// surfacing as a <c>tool.result</c> rather than a staged <c>Confirmation</c>.
    /// </summary>
    IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        bool autoExecute = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default,
        // The current content of an open blueprint draft this turn carries (see RunAsync). Last params to
        // keep existing positional callers unshifted.
        string? openDraftYaml = null,
        string? userDisplay = null);

    /// <summary>
    /// Executes a previously-staged destructive operation after a human has confirmed
    /// it. This is the model-independent execution gate: the model only ever STAGES
    /// install/uninstall (via the dispatcher); the operation runs only here.
    /// <para>
    /// The target is re-validated against live inventory before executing (the
    /// instance still exists for an uninstall; the blueprint still exists and the
    /// requested name doesn't now collide for an install). That re-validation also
    /// guards against replay of a stateless confirmation token within its lifetime.
    /// </para>
    /// </summary>
    /// <param name="confirmation">The staged operation to execute.</param>
    /// <param name="canPerformActions">
    /// Whether the confirming principal is authorized — computed FRESH at confirm
    /// time by the host (e.g. the clicker's current role), never carried from staging
    /// or trusted from a token. When false, the operation is refused.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A success result whose value is the human-readable outcome, or a failure whose
    /// error explains why it was refused or could not complete.
    /// </returns>
    Task<Result<string>> ConfirmAsync(
        PendingConfirmation confirmation,
        bool canPerformActions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes an assistant-authored blueprint after the user reviewed/edited it in the chat (the
    /// human-review checkpoint — <c>assistant-blueprint-review-plan.md</c>): re-validates the possibly-edited
    /// YAML, then runs the test-install → verify → repair → keep/stash pipeline. Separate from
    /// <see cref="ConfirmAsync"/> because its result is a rich <see cref="Envelope.ToolResult{TData}"/> card
    /// (a Verified card, or a fresh <see cref="Blueprints.BlueprintAuthoringOutcome.DraftReady"/> card for
    /// another edit when the autonomous repair loop exhausts) rather than a one-line confirmation string — a
    /// card surface renders that outcome and, on DraftReady, stages a fresh Blueprint confirmation for the
    /// re-edit loop.
    /// <para><paramref name="canPerformActions"/> is computed FRESH at confirm time by the host (never
    /// trusted from the staging token); when false the finalize is refused.</para>
    /// </summary>
    Task<Envelope.ToolResult<Blueprints.BlueprintAuthoringData>> FinalizeBlueprintAsync(
        string game,
        string editedYaml,
        bool canPerformActions,
        CancellationToken cancellationToken = default);
}
