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

    /// <summary>A tool finished; carries its name + a summary of the result.</summary>
    ToolResult,

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
    string? ToolCallId = null)
{
    public static AssistantStreamEvent Token(string delta) => new(AssistantEventKind.Token, Text: delta);
    public static AssistantStreamEvent Thinking(string delta) => new(AssistantEventKind.Thinking, Text: delta);
    public static AssistantStreamEvent ToolStart(Tool tool, IReadOnlyDictionary<string, string?> arguments, string? id = null) =>
        new(AssistantEventKind.ToolStart, ToolName: tool, ToolArguments: arguments, ToolCallId: id);
    public static AssistantStreamEvent ToolResult(Tool tool, string summary, string? id = null) =>
        new(AssistantEventKind.ToolResult, ToolName: tool, ToolSummary: summary, ToolCallId: id);
    public static AssistantStreamEvent Confirmation(PendingConfirmation confirmation) =>
        new(AssistantEventKind.Confirmation, StagedConfirmation: confirmation);

    /// <summary>The terminal success event: the full reply plus the turn's token usage (if any).</summary>
    public static AssistantStreamEvent Final(string text, LlmUsage? usage = null) =>
        new(AssistantEventKind.Final, Text: text, Usage: usage);
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
    /// <param name="requestedTools">
    /// Optional tool names the client wants available this turn. When null or empty,
    /// all authorized tools are used. Invalid names cause a hard error; names the
    /// caller isn't authorized for are silently removed.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the turn.</param>
    Task<AssistantResult> RunAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming counterpart to <see cref="RunAsync"/>: same policy (tool offering, gate, blast
    /// caps, confirmation staging) but yields <see cref="AssistantStreamEvent"/>s as the turn
    /// unfolds — reply <c>Token</c>s, tool-round <c>Status</c>, then any staged
    /// <c>Confirmation</c>s, ending with one terminal <c>Final</c> or <c>Error</c>. Authority is
    /// still passed in (the host derives it from the verified principal), never inferred here.
    /// </summary>
    IAsyncEnumerable<AssistantStreamEvent> RunStreamAsync(
        string conversationId,
        string userPrompt,
        bool canPerformActions,
        bool think = false,
        IReadOnlyList<string>? requestedTools = null,
        CancellationToken cancellationToken = default);

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
}
