using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// A chat turn: the user's message and optional tool selection. The conversation key for
/// memory is derived server-side from the authenticated principal (<c>web:{discordUserId}</c>)
/// — it is NOT client-supplied, so one caller can't read or poison another's history.
/// </summary>
public sealed record TurnRequest(
    string? Prompt,
    bool? Think = null,
    IReadOnlyList<string>? Tools = null,
    // The per-turn "let the assistant act" toggle (the SPA's chat switch). It is INTENT, not
    // authority: actions only happen when this is true AND the caller is authorized. On the trusted
    // relay path authority is the api's verified tier (X-Relay-Can-Act, which already folds in this
    // toggle); on the direct session path it is the caller's Discord action role, ANDed with this flag.
    bool? Actions = null);

/// <summary>
/// A tool parameter, as returned by <c>GET /tools</c>.
/// </summary>
public sealed record ToolParameterDto(
    string Name,
    string Description,
    bool Required,
    string Type,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>
/// A tool definition, as returned by <c>GET /tools</c>.
/// </summary>
public sealed record ToolDto(
    string Name,
    string Description,
    IReadOnlyList<ToolParameterDto> Parameters);

/// <summary>
/// Token accounting for a turn, in tokens (never a percentage): the prompt the model evaluated,
/// what it generated, the sum (<see cref="UsedTokens"/>), the configured <see cref="ContextWindow"/>
/// (num_ctx), and what's left. Lets the SPA render "used / available". Null when the backend
/// reported no counts.
/// </summary>
public sealed record UsageDto(
    int PromptTokens, int ResponseTokens, int UsedTokens, int ContextWindow, int RemainingTokens)
{
    public static UsageDto? From(LlmUsage? usage) => usage is null
        ? null
        : new UsageDto(
            usage.PromptTokens, usage.ResponseTokens, usage.UsedTokens,
            usage.ContextWindow, usage.RemainingTokens);
}

/// <summary>
/// One destructive op the assistant staged this turn, awaiting confirmation. The opaque
/// <see cref="Token"/> is what the client POSTs back to <c>/confirm</c>.
/// <see cref="ConfigKey"/>/<see cref="ConfigValue"/> are populated only for the
/// <c>setconfig</c> kind so a client can render "set key = value on target"; null otherwise.
/// </summary>
public sealed record ConfirmationDto(
    string Kind, string Target, string? InstanceName, string Token,
    string? ConfigKey = null, string? ConfigValue = null);

/// <summary>The assistant's reply plus any staged confirmations and the turn's token usage.</summary>
public sealed record TurnResponse(
    string Text, IReadOnlyList<ConfirmationDto> Confirmations, UsageDto? Usage = null);

/// <summary>A confirmation submission: the token issued by a prior <c>/turn</c>.</summary>
public sealed record ConfirmRequest(string? Token);

/// <summary>The outcome of executing a confirmed operation.</summary>
public sealed record ConfirmResponse(string Text, bool Success);

/// <summary>The Discord authorize URL the SPA should navigate the browser to.</summary>
public sealed record LoginUrlResponse(string Url);

/// <summary>The OAuth callback payload the SPA POSTs back after Discord redirects to it.</summary>
public sealed record AuthCallbackRequest(string? Code, string? State);

/// <summary>A minted web session: the bearer token to send on subsequent calls + the display name.</summary>
public sealed record AuthSessionResponse(string Token, string DisplayName);

/// <summary>Who the caller is, and whether they may perform actions right now (for the SPA's UI).</summary>
public sealed record MeResponse(string UserId, string DisplayName, bool CanPerformActions);

// --- Server-Sent Events payloads (§5·a) -------------------------------------------------------
// A client that sends `Accept: text/event-stream` to /turn gets these as the `data:` of the
// canonical §5·a typed events (architecture.html §5·a / toolbox-plan §5·a / keystone O1) instead
// of one buffered TurnResponse: `text.delta` / `tool.start` / `tool.result` / `command.proposed` /
// `done` / `error`, plus the opt-in additive `thinking.delta`. Each frame is emitted with BOTH the
// SSE `event:` name AND an in-band `type` discriminator (injected by SseTurnWriter from the same
// constant) so a client can key on either. (`command.verified` is NOT a turn-stream event — it
// rides the API's M3 command path; the SPA composes it client-side. See kgsm-llm/docs/m7-sse-5a-spec.md.)

/// <summary>The canonical §5·a turn-stream event names — the SSE `event:` line AND the in-band `type`.</summary>
public static class TurnStream
{
    public const string TextDelta = "text.delta";
    public const string ThinkingDelta = "thinking.delta";
    public const string ToolStart = "tool.start";
    public const string ToolResult = "tool.result";
    public const string CommandProposed = "command.proposed";
    public const string Done = "done";
    public const string Error = "error";
}

/// <summary>`text.delta` — one incremental slice of the assistant's reply text.</summary>
public sealed record TokenEvent(string Text);

/// <summary>`thinking.delta` — one incremental slice of the model's internal reasoning (opt-in via `think`).</summary>
public sealed record ThinkingEvent(string Text);

/// <summary>
/// `tool.start` — a tool is about to run. <see cref="Id"/> is the synthesised correlation id that
/// pairs this with its <see cref="ToolResultEvent"/>; `label` is omitted (no honest source — the SPA
/// derives a display name from <see cref="Tool"/>); `arguments` is additive over the §5·a example.
/// </summary>
public sealed record ToolStartEvent(string Id, string Tool, IReadOnlyDictionary<string, string?> Arguments);

/// <summary>
/// `tool.result` — a tool finished. <see cref="Id"/> pairs it with its <see cref="ToolStartEvent"/>.
/// <see cref="Summary"/> is the model's grounding text (the dispatcher's string output), always
/// present. <see cref="Result"/> is the §5·a structured card (toolbox-plan §5·c, a
/// <c>ToolResultCard</c> projected from the tool's <c>ToolResult&lt;K,D&gt;</c>) — Phase 2, present
/// only for the tools that have a real card (today: <c>run_health_check</c>); omitted from the
/// frame entirely (not <c>null</c>) for summary-only tools, so a thin client is unaffected.
/// </summary>
public sealed record ToolResultEvent(
    string Id,
    string Tool,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result = null);

/// <summary>The §5·a <c>command.proposed.subject</c> — what the staged op is about.</summary>
public sealed record CommandSubject(string Resource, string Id);

/// <summary>
/// `command.proposed` — a destructive op staged this turn, awaiting human confirmation, in the §5·a
/// shape. <see cref="Verb"/> is the normalised API verb token (the SPA routes a confirm to the M3
/// command path — fork (a)); <see cref="Token"/> is the host-minted confirmation token, RETAINED
/// (additive beyond §5·a) for the surfaces that execute via the assistant's own <c>/confirm</c>
/// (Discord/CLI + verbs without an API endpoint yet). <see cref="ConfigKey"/>/<see cref="ConfigValue"/>
/// are populated only for the <c>set_config</c> verb.
/// </summary>
public sealed record CommandProposedEvent(
    string Id, string Verb, CommandSubject Subject, string Confirm, string Token,
    string? Reason = null, string? ConfigKey = null, string? ConfigValue = null);

/// <summary>
/// `done` — terminal success; the full assembled reply plus the turn's token <see cref="Usage"/>
/// (used / available, in tokens) for the SPA's context meter (both fields additive over §5·a's empty `done`).
/// </summary>
public sealed record DoneEvent(string Text, UsageDto? Usage = null);

/// <summary>
/// `error` — terminal failure surfaced in-band (the stream is already HTTP 200). <see cref="Code"/>
/// is a coarse closed bucket (e.g. <c>assistant_failed</c>); <see cref="Message"/> carries the real detail.
/// </summary>
public sealed record StreamErrorEvent(string Code, string Message);
