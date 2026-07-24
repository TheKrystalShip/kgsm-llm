using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// A chat turn: the user's message and optional tool selection. The conversation key for memory is
/// ALWAYS namespaced under a server-side, authenticated identity (<c>web:{discordUserId}</c>) — WHO the
/// caller is is never client-supplied, so one caller can't read or poison another's history. An optional
/// per-chat <see cref="ConversationId"/> sub-scopes that user's OWN memory into separate context windows.
/// </summary>
public sealed record TurnRequest(
    string? Prompt,
    bool? Think = null,
    IReadOnlyList<string>? Tools = null,
    // The per-turn "let the assistant act" toggle (the SPA's chat switch). It is INTENT, not
    // authority: actions only happen when this is true AND the caller is authorized. On the trusted
    // relay path authority is the api's verified tier (X-Relay-Can-Act, which already folds in this
    // toggle); on the direct session path it is the caller's Discord action role, ANDed with this flag.
    bool? Actions = null,
    // The per-CHAT conversation id (the SPA's "new chat" identity). It does NOT carry identity — memory
    // is always keyed web:{serverUserId}[:{ConversationId}], the user id resolved server-side — so it only
    // partitions THIS user's history into separate context windows, never reaching another caller's.
    // Sanitised + length-capped server-side; null ⇒ the bare per-user conversation (one running thread),
    // preserving the prior single-context behaviour. On the trusted-relay path the api forwards this as
    // the X-Relay-Conversation-Id header instead of this body field.
    string? ConversationId = null);

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

// --- Conversation history read-back (the reverse path) ----------------------------------------
// The write path keys per-user, per-chat memory web:{userId}[:{chatId}] from the verified identity.
// These read endpoints close the loop: a surface (the SPA via the api relay) lists the caller's own
// past chats and loads one back, so history survives a new browser/device — it lives server-side, not
// only in the client. WHO the caller is stays server-derived (the principal), so a caller can only ever
// enumerate/read its OWN conversations. The transcript DTOs reuse the §5·a field vocabulary (tool/
// thinking/usage) so a client re-scaffolds an old conversation through the SAME render path it uses for
// a live turn — no second schema.

/// <summary>
/// One row of <c>GET /conversations</c>: a past chat in the caller's namespace. <see cref="Id"/> is the
/// per-chat sub-scope the client sent as <c>conversationId</c> (empty for the legacy bare per-user
/// conversation), so a client joins this list to its own chats by id and fetches one by it.
/// <see cref="Title"/> is the first prompt (null for an empty conversation); the timestamps + count let
/// the client order and label without loading the transcript.
/// </summary>
public sealed record ConversationSummaryDto(
    string Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TurnCount);

/// <summary>
/// <c>GET /conversations/{id}</c>: the full transcript for one chat, oldest-first — the append-only log
/// verbatim (turns + non-destructive compaction checkpoints), so the client renders the WHOLE history as
/// it happened (compaction affects only what the model replays, never what's shown).
/// </summary>
public sealed record ConversationHistoryDto(
    string Id,
    IReadOnlyList<ConversationHistoryEntryDto> Entries);

/// <summary>
/// One history entry. <see cref="Kind"/> is <c>turn</c> (then <see cref="Turn"/> is set) or
/// <c>checkpoint</c> (then <see cref="CheckpointSummary"/> is set — a compaction recap the client shows
/// as a divider).
/// </summary>
public sealed record ConversationHistoryEntryDto(
    string Kind,
    DateTimeOffset CreatedAt,
    ConversationTurnDto? Turn = null,
    string? CheckpointSummary = null);

/// <summary>
/// One completed turn, in the §5·a vocabulary: the user <see cref="Prompt"/>, the assistant
/// <see cref="Final"/> reply, whether <see cref="Think"/> was on + the <see cref="Thinking"/> text it
/// produced, the ordered <see cref="Tools"/> trajectory, the token <see cref="Usage"/>, and the
/// <see cref="Outcome"/> ("ok"/"error"/"capHit"/"cancelled", lower-cased).
/// </summary>
public sealed record ConversationTurnDto(
    string Prompt,
    string? Final,
    bool Think,
    string? Thinking,
    IReadOnlyList<ConversationToolDto> Tools,
    UsageDto? Usage,
    string Outcome);

/// <summary>
/// One tool call within a turn, mirroring the live <see cref="ToolResultEvent"/>: the
/// <see cref="Tool"/> name, the <see cref="Arguments"/> the model passed, the model-facing
/// <see cref="Summary"/> text, and the optional structured §5·a <see cref="Result"/> card (present only
/// for tools that emit one). Same field names as the wire event so a client re-scaffolds the tool pill
/// from history exactly as it does live.
/// </summary>
public sealed record ConversationToolDto(
    string Tool,
    IReadOnlyDictionary<string, string?> Arguments,
    string Summary,
    object? Result = null);

/// <summary>
/// <c>POST /conversations/{id}/compact</c>: the outcome of an on-demand compaction. Mirrors
/// <c>CompactionOutcome</c> for the HTTP surface: <see cref="Compacted"/> is false when there was nothing
/// worth compacting (an empty/short conversation), in which case the history was left untouched and
/// <see cref="MessagesCompacted"/> is 0 / <see cref="Summary"/> is empty. Non-destructive either way —
/// compaction appends a checkpoint and only changes what the model replays, never what history shows.
/// </summary>
public sealed record CompactionResultDto(
    bool Compacted,
    int MessagesCompacted,
    string Summary);

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
    public const string Progress = "progress";
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

/// <summary>
/// `progress` — one step of a long-running tool's own progress, reported WHILE the tool is still
/// executing (today: only <c>create_blueprint</c>'s research→draft→install→verify→teardown stages), so
/// a client can render a live stepper instead of waiting silently for the tool's single terminal
/// <see cref="ToolResultEvent"/>. Never terminal on its own — it is always followed, later in the same
/// turn, by that tool's own <c>tool.start</c>/<c>tool.result</c> pair.
/// <see cref="Id"/> is the tool-call correlation id used by <c>tool.start</c>/<c>tool.result</c> when the
/// emitter can supply one; the ambient sink a step is reported through (<c>ITurnProgress</c>) has no
/// access to that id (it is minted deep inside the generic agent loop, never threaded down into tool
/// execution), so <see cref="Id"/> is omitted from the frame today — a step still correlates to its
/// tool by <see cref="Tool"/> and by turn order. <see cref="Status"/> is <c>"active"</c> — a step is
/// reported when it begins; there is no separate "done" frame per step.
/// </summary>
public sealed record ProgressEvent(
    string Tool,
    string Key,
    string Label,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null);

/// <summary>The §5·a <c>command.proposed.subject</c> — what the staged op is about.</summary>
public sealed record CommandSubject(string Resource, string Id);

/// <summary>
/// The `write_file` preview block on `command.proposed`: the relative <see cref="Path"/> and
/// <see cref="ProposedContent"/> (the COMPLETE new file content, bounded by the write cap) so a client
/// can render a diff before the user confirms. Rides the frame body, never the token (the token instead
/// carries an opaque pending-write id — see <c>PendingWriteTokenSwap</c> — because a 10 MB body can't
/// ride a stateless HMAC token).
/// </summary>
public sealed record CommandFile(string Path, string ProposedContent);

/// <summary>
/// `command.proposed` — a destructive op staged this turn, awaiting human confirmation, in the §5·a
/// shape. <see cref="Verb"/> is the normalised API verb token (the SPA routes a confirm to the M3
/// command path — fork (a)); <see cref="Token"/> is the host-minted confirmation token, RETAINED
/// (additive beyond §5·a) for the surfaces that execute via the assistant's own <c>/confirm</c>
/// (Discord/CLI + verbs without an API endpoint yet). <see cref="ConfigKey"/>/<see cref="ConfigValue"/>
/// are populated only for the <c>set_config</c> verb. <see cref="InstanceName"/> is the optional
/// custom name for an <c>install</c> (the new instance the user asked for); null for every other verb
/// and for an unnamed install (kgsm then auto-names it). A surface that creates via an API endpoint
/// passes it through so a named install lands the name the user asked for rather than silently dropping it.
/// <see cref="File"/> is populated only for the <c>write_file</c> verb — the diff-preview payload;
/// null for every other verb.
/// </summary>
public sealed record CommandProposedEvent(
    string Id, string Verb, CommandSubject Subject, string Confirm, string Token,
    string? Reason = null, string? ConfigKey = null, string? ConfigValue = null,
    string? InstanceName = null, CommandFile? File = null);

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
