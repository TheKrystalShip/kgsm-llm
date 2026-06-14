namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// A chat turn: just the user's message. The conversation key for memory is derived
/// server-side from the authenticated principal (<c>web:{discordUserId}</c>) — it is NOT
/// client-supplied, so one caller can't read or poison another's history.
/// </summary>
public sealed record TurnRequest(string? Prompt);

/// <summary>
/// One destructive op the assistant staged this turn, awaiting confirmation. The opaque
/// <see cref="Token"/> is what the client POSTs back to <c>/confirm</c>.
/// <see cref="ConfigKey"/>/<see cref="ConfigValue"/> are populated only for the
/// <c>setconfig</c> kind so a client can render "set key = value on target"; null otherwise.
/// </summary>
public sealed record ConfirmationDto(
    string Kind, string Target, string? InstanceName, string Token,
    string? ConfigKey = null, string? ConfigValue = null);

/// <summary>The assistant's reply plus any staged confirmations.</summary>
public sealed record TurnResponse(string Text, IReadOnlyList<ConfirmationDto> Confirmations);

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

// --- Server-Sent Events payloads --------------------------------------------------------------
// A client that sends `Accept: text/event-stream` to /turn gets these as the `data:` of the
// canonical §5a typed events (toolbox-plan §5a / keystone O1) instead of one buffered
// TurnResponse: `text.delta` / `tool.start` / `tool.result` / `command.proposed` / `done` /
// `error`. The `command.proposed` event reuses ConfirmationDto. (`command.verified` belongs to the
// separate command-execution flow, not the turn stream — see toolbox-plan §5·d.)

/// <summary>`event: text.delta` — one incremental slice of the assistant's reply text.</summary>
public sealed record TokenEvent(string Delta);

/// <summary>`event: tool.start` — a tool is about to run; its name + arguments.</summary>
public sealed record ToolStartEvent(string Tool, IReadOnlyDictionary<string, string?> Arguments);

/// <summary>
/// `event: tool.result` — a tool finished. V1 is the minimal envelope: <see cref="Summary"/> is the
/// model's grounding text (the dispatcher's string output). The full ToolResult&lt;K,D&gt; `data`
/// card (toolbox-plan §5) is deferred until a surface consumes it.
/// </summary>
public sealed record ToolResultEvent(string Tool, string Summary);

/// <summary>`event: done` — terminal success; carries the full assembled reply text.</summary>
public sealed record DoneEvent(string Text);

/// <summary>`event: error` — terminal failure surfaced in-band (the stream is already HTTP 200).</summary>
public sealed record StreamErrorEvent(string Error);
