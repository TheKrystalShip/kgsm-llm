using System.Text.Json;
using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Assistant.Status;
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
    // The per-turn AUTO-RUN toggle (the SPA's chat switch). It is INTENT, not authority: it asks for
    // lifecycle commands to run with no confirmation step, and is ANDed with the caller's admin tier,
    // so it can only ever narrow. It does NOT gate whether the assistant may PROPOSE an action —
    // proposing is an operator capability and the user confirms each one, so gating that on a toggle
    // only hides the button. On the trusted-relay path the api forwards this intent as
    // X-Relay-Auto-Act instead and this field is not read.
    bool? Actions = null,
    // The per-CHAT conversation id (the SPA's "new chat" identity). It does NOT carry identity — memory
    // is always keyed web:{serverUserId}[:{ConversationId}], the user id resolved server-side — so it only
    // partitions THIS user's history into separate context windows, never reaching another caller's.
    // Sanitised + length-capped server-side; null ⇒ the bare per-user conversation (one running thread),
    // preserving the prior single-context behaviour. On the trusted-relay path the api forwards this as
    // the X-Relay-Conversation-Id header instead of this body field.
    string? ConversationId = null,
    // The CURRENT content of a blueprint draft the user is reviewing in the editor, when this turn carries
    // one (the SPA sends what's in the Monaco card, edits included). When present, the assistant offers
    // revise_blueprint and injects this content into the turn so the model can actually change the draft
    // from chat — the only way to edit it, and what stops it fabricating "I updated the draft". Null on an
    // ordinary turn. Not identity-bearing; re-validated when a revision is saved.
    string? DraftYaml = null);

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

/// <summary>A confirmation submission: the token issued by a prior <c>/turn</c>, plus (for a blueprint
/// finalize) the possibly-edited draft the user reviewed in the chat. <see cref="EditedContent"/> is the
/// Monaco editor's content on Save — used verbatim for a <c>blueprint</c> confirmation (re-validated
/// server-side); null for every other kind, and null on a blueprint save that didn't edit (the staged
/// draft is rehydrated as the fallback).</summary>
public sealed record ConfirmRequest(string? Token, string? EditedContent = null);

/// <summary>The outcome of executing a confirmed operation. For a blueprint finalize, <see cref="Card"/>
/// carries the rich outcome card (a Verified card, or a fresh DraftReady card when the repair loop
/// exhausts / the edit was invalid) and <see cref="Confirmations"/> carries a fresh Blueprint token for the
/// re-edit loop; both are null for every other kind, whose outcome is just <see cref="Text"/>.
/// <para>
/// <see cref="Success"/> is true only when the operation's postcondition was observed, or when the engine
/// reported success for a verb that has none — never for a lifecycle command that was accepted but never
/// reached its end state. <see cref="Outcome"/> carries which of those it was.
/// </para></summary>
public sealed record ConfirmResponse(
    string Text, bool Success,
    JsonElement? Card = null,
    IReadOnlyList<ConfirmationDto>? Confirmations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfirmOutcomeDto? Outcome = null);

/// <summary>
/// What is known about a confirmed operation, beyond the pass/fail of <see cref="ConfirmResponse.Success"/>.
/// <see cref="Verdict"/> distinguishes an observed end state (<c>settled</c>) from an engine-reported success
/// with nothing to observe (<c>accepted</c>), from one that ran without reaching its end state
/// (<c>notSettled</c>), from one whose end state could not be read (<c>unknown</c>). A client renders the
/// verdict rather than parsing <see cref="ConfirmResponse.Text"/> to guess which happened.
/// </summary>
/// <param name="Verdict">The outcome class, camelCase.</param>
/// <param name="Verb">The imperative verb, when the outcome came from a known operation.</param>
/// <param name="Instance">The instance targeted, when there was one.</param>
/// <param name="ObservedState">
/// The run state measured afterwards (<c>running</c> / <c>stopped</c> / <c>unknown</c>) for the verbs that
/// have a run-state postcondition; omitted for the rest. <c>unknown</c> means the read failed — it is never
/// a stand-in for "not running".
/// </param>
/// <param name="Reason">Why the state could not be read, or why the operation failed.</param>
public sealed record ConfirmOutcomeDto(
    string Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Verb = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObservedState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    /// <summary>Projects the domain outcome onto the wire shape (enum names camelCased by the writer's options).</summary>
    public static ConfirmOutcomeDto From(ConfirmOutcome outcome) => new(
        Camel(outcome.Verdict.ToString()),
        outcome.Verb,
        outcome.Instance,
        outcome.ObservedState is { } s ? Camel(s.ToString()) : null,
        outcome.Reason);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>
/// A minted session: the bearer to send on subsequent calls, the refresh token that buys the next
/// one, and when each dies.
/// </summary>
/// <remarks>
/// Both expiry instants travel so a client can refresh <em>before</em> a call fails rather than
/// discovering the lapse as a 401 mid-turn. They are the mint-time values, not a re-derivation — a
/// client is not asked to decode a token it has no business parsing.
/// </remarks>
public sealed record AuthSessionResponse(
    string Verdict,
    string? Tier,
    string? Token,
    string? Refresh,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshExpiresAt,
    string UserId,
    string DisplayName);

/// <summary>What a client presents to trade a refresh token for a fresh pair.</summary>
public sealed record RefreshRequest(string? Refresh);

/// <summary>
/// Who the caller is and what they may do right now. <see cref="Tier"/> is re-derived from Discord,
/// not read off the bearer, so it reflects a role change without a new sign-in.
/// </summary>
public sealed record MeResponse(
    string UserId, string DisplayName, string Tier, bool CanPerformActions);

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

// ---- the review surface (GET /admin/conversations…) ---------------------------------------------
// An administrator reading OTHER users' conversations, to judge where the assistant needs tuning.
// Two levels: who has talked to it, and which conversations one of them holds. The transcript itself
// reuses ConversationHistoryDto above verbatim — a reviewed transcript is the same shape as your own,
// so a client renders it through the very same path, read-only.

/// <summary>
/// One row of <c>GET /admin/conversations/users</c>: everyone who has talked to this assistant on the
/// web surface, with the size and span of their footprint. <see cref="UserId"/> is the opaque Discord
/// id the conversations are keyed by; <see cref="DisplayName"/> is the newest name any of their turns
/// recorded and is <c>null</c> for conversations that predate name capture — a client shows the id
/// then, never a name guessed from it.
/// </summary>
public sealed record AdminConversationUserDto(
    string UserId,
    string? DisplayName,
    int ConversationCount,
    int DeletedCount,
    int TurnCount,
    DateTimeOffset FirstActivityAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// One row of <c>GET /admin/conversations?user={userId}</c>. <see cref="Id"/> is an OPAQUE handle for
/// the transcript endpoint, not a key the client composes — the stored id is the store's business, and
/// keeping it opaque is what stops a client addressing conversations by construction.
/// <see cref="Deleted"/> marks one its owner hid (the transcript is retained regardless);
/// <see cref="ErrorTurns"/>/<see cref="CapHitTurns"/> are the tuning signal — where the assistant
/// failed or ran out of iterations without answering.
/// </summary>
public sealed record AdminConversationDto(
    string Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TurnCount,
    bool Deleted,
    int ErrorTurns,
    int CapHitTurns,
    int NegativeTurns);

/// <summary>
/// <c>GET /admin/conversations/{id}</c>: whose conversation it is, its summary row, and the transcript
/// in the SAME <see cref="ConversationHistoryEntryDto"/> shape the caller's own history returns.
/// </summary>
public sealed record AdminConversationHistoryDto(
    AdminConversationUserRefDto User,
    AdminConversationDto Conversation,
    IReadOnlyList<ConversationHistoryEntryDto> Entries);

/// <summary>Who a reviewed conversation belongs to: the opaque id, and a recorded name if there is one.</summary>
public sealed record AdminConversationUserRefDto(string UserId, string? DisplayName);

/// <summary>
/// One tool's behaviour across the corpus, for <c>GET /admin/conversations/stats</c>.
/// <see cref="Known"/> is the assistant's own answer to "does this tool exist": the recorded name is
/// checked against the shipped catalog here, in the surface that owns one, because the store that
/// derives the numbers is deliberately domain-blind. <c>false</c> means the model called a tool that
/// has never existed — the sharpest tuning signal the corpus carries, and the reason the name is
/// reported rather than dropped.
/// </summary>
public sealed record AdminToolStatDto(
    string Name,
    bool Known,
    int Calls,
    long MedianMs,
    long MaxMs,
    int FailedCalls);

/// <summary>
/// One system-prompt version's slice, for <c>GET /admin/conversations/stats</c>. Bucketing by the hash
/// is what makes "did that prompt edit help?" answerable from the log alone. <see cref="Hash"/> is
/// <c>null</c> for turns recorded without one; <see cref="MedianMs"/> is <c>null</c> when none of the
/// version's turns was timed.
/// </summary>
public sealed record AdminPromptVersionDto(
    string? Hash, int Turns, int OkTurns, long? MedianMs, int NegativeTurns, int RatedTurns);

/// <summary>Turns started on one UTC day (<c>yyyy-MM-dd</c>), for the activity strip.</summary>
public sealed record AdminDailyTurnsDto(string Date, int Turns);

/// <summary>
/// <c>GET /admin/conversations/stats</c>: the whole-corpus roll-up behind the operator overview,
/// derived from the same append-only log the transcripts come from.
/// <para>
/// Counts are counts and read <c>0</c> when the thing did not happen. Every distribution figure is
/// <b>nullable and null when nothing was measured</b> — a corpus with no timed turn reports a null
/// median rather than a zero, because a zero here would read as "instant" and that would be a
/// fabricated measurement.
/// </para>
/// </summary>
public sealed record AdminConversationStatsDto(
    int Conversations,
    int DeletedConversations,
    int Actors,
    int Turns,
    int OkTurns,
    int ErrorTurns,
    int CapHitTurns,
    int CancelledTurns,
    int UnrecordedOutcomeTurns,
    long? MedianTurnMs,
    long? P95TurnMs,
    long? MaxTurnMs,
    int? MedianIterations,
    int? MaxIterations,
    double? MedianContextPercent,
    double? MaxContextPercent,
    int? ContextWindow,
    int ThinkingTurns,
    int TurnsWithoutTool,
    int ToolCalls,
    IReadOnlyList<AdminToolStatDto> Tools,
    IReadOnlyList<AdminPromptVersionDto> PromptVersions,
    IReadOnlyList<AdminDailyTurnsDto> Activity,
    AdminAssistantRuntimeDto Runtime,
    int RatedTurns,
    int PositiveTurns,
    int NegativeTurns,
    double? SatisfactionPercent,
    IReadOnlyList<AdminFeedbackNoteDto> FeedbackNotes);

/// <summary>
/// One thumbs-down and what its author wrote about it — the only record of WHY an answer failed, and
/// the first thing a tuning pass reads. <see cref="ConversationId"/> is the same opaque handle the
/// listing mints, so a reviewer can open the conversation the complaint came from;
/// <see cref="TurnId"/> names the turn inside it, and <see cref="Prompt"/> is what was asked.
/// </summary>
public sealed record AdminFeedbackNoteDto(
    string ConversationId,
    long TurnId,
    string Note,
    string? Prompt,
    DateTimeOffset At);

/// <summary>
/// What the assistant is currently configured to be, alongside the numbers describing what it did.
/// The pairing is the point: "median 2 tool steps" only means something next to "the cap is 16", and
/// "9.5% of the window" only means something next to which window. Read from the live options, so it
/// describes the process answering right now — not whatever produced the older turns in the corpus.
/// </summary>
public sealed record AdminAssistantRuntimeDto(
    string Model,
    int ContextWindow,
    int MaxIterations,
    bool ActionsEnabled);

/// <summary>
/// One history entry. <see cref="Kind"/> is <c>turn</c> (then <see cref="Turn"/> is set) or
/// <c>checkpoint</c> (then <see cref="CheckpointSummary"/> is set — a compaction recap the client shows
/// as a divider).
/// </summary>
public sealed record ConversationHistoryEntryDto(
    string Kind,
    DateTimeOffset CreatedAt,
    ConversationTurnDto? Turn = null,
    string? CheckpointSummary = null,
    DateTimeOffset? StartedAt = null,
    long TurnId = 0,
    TurnFeedbackDto? Feedback = null);

/// <summary>
/// The verdict its owner left on one turn. <see cref="Rating"/> is <c>"up"</c> or <c>"down"</c>;
/// <see cref="Note"/> is what they wrote about a thumbs-down, and is <c>null</c> when they wrote
/// nothing. An unrated turn carries no <c>TurnFeedbackDto</c> at all rather than a neutral one — "not
/// asked" and "answered indifferently" are different facts.
/// </summary>
public sealed record TurnFeedbackDto(string Rating, string? Note, DateTimeOffset At);

/// <summary>
/// <c>POST /conversations/{id}/turns/{turnId}/feedback</c>: how the caller judged one of their own
/// answers. <see cref="Rating"/> is <c>"up"</c>, <c>"down"</c>, or <c>null</c> to withdraw a verdict
/// already left. <see cref="Note"/> is the optional "what went wrong", offered only on a thumbs-down.
/// </summary>
public sealed record TurnFeedbackRequest(string? Rating, string? Note);

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

// --- Server-Sent Events payloads ---------------------------------------------------------------
// A client that sends `Accept: text/event-stream` to /turn gets these as the `data:` of typed
// events instead of one buffered TurnResponse: `text.delta` / `tool.start` / `tool.result` /
// `progress` / `command.proposed` / `done` / `error`, plus the opt-in `thinking.delta`. Each frame
// carries BOTH the SSE `event:` name AND an in-band `type` discriminator (injected by SseTurnWriter
// from the same constant) so a client can key on either. `command.verified` is emitted by no
// backend stream: a client composes its own verification block from the outcome it received.
//
// These shapes are the public compatibility boundary — two SPAs consume them on independent deploy
// cadences. What is additive and what is breaking: docs/wire-contract.md.

/// <summary>The turn-stream event names — the SSE `event:` line AND the in-band `type`.</summary>
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

    /// <summary>`result` — the terminal frame of a STREAMED <c>/confirm</c> blueprint finalize: carries the
    /// whole <see cref="ConfirmResponse"/> (the same buffered callers get) once the test-install → verify →
    /// repair pipeline lands. Distinct from <see cref="Done"/> (a turn's assembled reply text); a finalize's
    /// outcome is the rich card + any re-edit token, not a text stream. Preceded by <see cref="Progress"/>
    /// steps and heartbeat comments that keep the minutes-long stream's socket alive.</summary>
    public const string Result = "result";
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
/// <para>
/// <see cref="TurnId"/> names the turn just recorded, so a client can act on the answer it received —
/// rating it — without inferring which turn was "the last one". It is <c>0</c> when the turn could not
/// be persisted; a client reads that as "not addressable" and offers nothing to act on.
/// </para>
/// </summary>
public sealed record DoneEvent(
    string Text, DateTimeOffset CompletedAt, UsageDto? Usage = null, long TurnId = 0);

/// <summary>
/// `error` — terminal failure surfaced in-band (the stream is already HTTP 200). <see cref="Code"/>
/// is a coarse closed bucket (e.g. <c>assistant_failed</c>); <see cref="Message"/> carries the real detail.
/// </summary>
public sealed record StreamErrorEvent(string Code, string Message);
