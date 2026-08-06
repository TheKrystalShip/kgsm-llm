# The assistant wire contract

**Contract version 1.0.**

The public HTTP contract between the assistant leaf and any browser client. Two clients consume
it — `kgsm-web`'s assistant dock and the standalone assistant SPA — on independent deploy
cadences, so the shapes here are the compatibility boundary: additive changes are free, anything
else is a version bump.

This document covers the two channels that carry an assistant interaction: the **turn stream**
(`POST /turn`) and the **confirm channel** (`POST /confirm`). Authentication, conversations and
the review surface are separate; see `CONFIGURATION.md` and `ARCHITECTURE.md`.

---

## 1 · Framing

Both channels speak Server-Sent Events when the caller sends `Accept: text/event-stream`, and
plain JSON otherwise. A streaming caller gets the richer contract; a buffered caller (the CLI, a
script) gets one terminal object.

Every SSE frame carries **both** the SSE `event:` name and an in-band `type` field with the same
value. A client keys on either:

```
event: text.delta
data: {"type":"text.delta","text":"Checking factorio-test…"}
```

Rules that hold for every frame on both channels:

- **An absent optional field is omitted, never `null`.** A client tests presence, not nullity.
- **Unknown frames and unknown fields are ignored, never fatal.** This is what makes an additive
  change safe to deploy ahead of its clients.
- **The response commits `200` on the first frame.** Any failure after that is an in-band `error`
  frame, never a status code. A failure *before* the first frame is an ordinary 4xx/5xx with a
  JSON body.
- **A line beginning `:` is a keep-alive comment** carrying no data. Standard SSE parsers drop it
  for free.
- Frames are serialized camelCase, enums as camelCase strings.

---

## 2 · The turn stream — `POST /turn`

Request body:

| Field | Meaning |
|---|---|
| `prompt` | required |
| `think` | opt into `thinking.delta` frames |
| `tools` | restrict the offered tool set |
| `actions` | the per-turn "let the assistant act" toggle — **intent, not authority**. It is ANDed with the caller's tier; it can only ever narrow |
| `conversationId` | partitions this user's own history into separate context windows. It carries no identity — memory is always keyed by the server-resolved user id |

### Frames

| Frame | Payload | Meaning |
|---|---|---|
| `text.delta` | `{ text }` | one slice of the reply |
| `thinking.delta` | `{ text }` | one slice of model reasoning; emitted only when `think` is set |
| `tool.start` | `{ id, tool, arguments }` | a tool is about to run |
| `tool.result` | `{ id, tool, summary, result? }` | that tool finished |
| `progress` | `{ tool, key, label, status, id? }` | a step *inside* a still-running tool |
| `command.proposed` | `{ id, verb, subject, confirm, token, reason?, configKey?, configValue?, instanceName?, file? }` | an operation staged for human confirmation |
| `error` | `{ code, message }` | terminal failure |
| `done` | `{ text, completedAt, usage?, turnId }` | terminal success |

A turn ends with exactly one `done` **or** one `error`.

### `tool.start` / `tool.result`

`id` pairs the two. It is synthesised (`tc_<n>`) because Ollama provides no native tool-call id;
without it a renderer cannot pair two calls to the *same* tool. `arguments` is the resolved
argument map.

`tool.start` carries **no display label**. The tool model has no human-authored label, so the
contract does not invent one — a client derives a display name from `tool`, or from the
description it already has from `GET /tools`.

`summary` is always present: the model-facing grounding text.

`result` is present **only when the tool has a structured source**, and is omitted entirely
otherwise. This is the never-fabricate rule at the wire: a tool whose output is an opaque string
(single-server `get_status`, `read_file`) is summary-only, and a tool whose read failed or came
back empty stays summary-only rather than emitting a hollow card.

When present, `result` is a card:

```json
{ "tool": "run_health_check",
  "confidence": "confirmed",
  "subject": { "resource": "server", "id": "factorio", "section": null },
  "data": { },
  "links": null }
```

`data` is **shaped per tool** — its schema belongs to that tool, not to this contract. A client
keys on `result.tool` to pick a renderer and **falls back to `summary` for a tool it does not
recognise**. That fallback is load-bearing: it is what lets the leaf light a card on a new tool
without waiting for either SPA to ship.

`subject.resource` is `server`, `blueprint`, or `host`. A host-scoped card's `subject.id` is
informational, not a route key — the leaf is standalone and has no cluster-wide host identity to
offer.

### `progress`

Reported by a long-running tool *while it is still executing*, so a client renders a live stepper
instead of a dead spinner. It is never terminal on its own — the tool's own
`tool.start` / `tool.result` pair still follows.

`status` is always `active`: a step is announced when it begins, and there is no per-step
completion frame. `id` is present only when the reporting path knows the tool-call id; a step
otherwise correlates by `tool` and by arrival order.

### `command.proposed`

The assistant **stages** every mutating operation and executes none of them inside a turn. One
frame per staged operation.

| Field | Meaning |
|---|---|
| `id` | `cmd_<n>`, turn-stable. A display correlation handle — **not** security-bearing |
| `verb` | `start`, `stop`, `restart`, `update`, `install`, `uninstall`, `backup`, `set_config`, `open_ports`, `write_file` |
| `subject` | `{ resource, id }` — `blueprint` for `install`, `server` for everything else |
| `confirm` | a human prompt composed from the staged operation |
| `token` | the opaque, security-bearing confirmation token. This is what `POST /confirm` takes |
| `configKey` / `configValue` | `set_config` (the key and value) and `open_ports` (the port spec, and `router` on the key when a UPnP forward is included) |
| `instanceName` | the custom name for an `install`; absent when kgsm auto-names |
| `file` | `write_file` only: `{ path, proposedContent }` — the complete new content, so a client can render a diff before the user confirms |

`token` and `file` are independent by design: a large file body rides the frame, never the
stateless token.

**Auto-run is the one exception to propose-only.** A turn authorised for auto-run executes the
lifecycle verbs inline instead of staging them; those emit no `command.proposed` and surface as an
ordinary `tool.start` / `tool.result` pair. `install`, `uninstall`, `set_config` and `write_file`
stay propose-only regardless.

### `done` and `error`

`done` carries the assembled reply, `completedAt`, optional `usage`
(`{ promptTokens, responseTokens, usedTokens, contextWindow, remainingTokens }`), and `turnId`.
`turnId` names the turn just recorded so a client can rate it without inferring which turn was the
last; it is **`0` when the turn could not be persisted**, which a client reads as "not
addressable" and offers no action on.

`error.code` is a coarse closed bucket; `message` carries the detail. The codes this contract
emits are:

| Code | Channel |
|---|---|
| `assistant_failed` | turn stream |
| `finalize_failed` | confirm channel |

A code is never narrowed to something that overstates what is known. A client that meets an
unrecognised code renders `message`.

### Not in this contract

**`command.verified` is emitted by no backend stream.** A client that renders a post-action
verification block composes it from the outcome it received for the operation it ran. It is named
here only so its absence is not read as an omission.

---

## 3 · The confirm channel — `POST /confirm`

Takes `{ token, editedContent? }`. `token` is the one carried by a `command.proposed` frame.
`editedContent` applies to a blueprint finalize — the reviewed, possibly edited draft; absent
means the staged draft is used.

Authority is **re-derived at confirm time**, never read off the token. A token staged by a
different user is refused with the same message as a malformed or expired one, so the response is
not an oracle for which case occurred.

### Buffered form

Every confirmation kind answers `{ text, success, card?, confirmations? }`.

- `text` — the outcome, human-readable.
- `success` — whether the operation succeeded.
- `card` — the rich outcome card, on the kinds that have one.
- `confirmations` — a fresh confirmation token when the outcome leaves something to confirm
  again. A blueprint finalize whose repair loop exhausts returns its draft this way, which is the
  re-edit loop.

### Streamed form

A blueprint finalize runs a test-install → boot → verify → bounded-repair pipeline: minutes of
work with long silent stretches. Streamed, it emits:

| Frame | Meaning |
|---|---|
| `progress` | the pipeline's own steps, same shape as the turn stream's |
| `: keepalive` | a comment every 15s, so no idle reaper drops the socket |
| `result` | terminal — carries the whole buffered response object |
| `error` | terminal failure after the status committed |

`result` is the confirm channel's terminal frame and is distinct from the turn stream's `done`: a
finalize's outcome is a card, not assembled text.

The other confirmation kinds answer buffered. A caller that asks for a stream on one of them
receives the buffered JSON response.

---

## 4 · Compatibility

The contract version at the top of this document changes when a client must change with it.

**Additive, no version change:** a new frame type; a new optional field; a new `verb`; a new
`error.code`; a card lit on a tool that had none; a new `data` shape under an existing card. All
of these are safe because clients ignore what they do not recognise and fall back to `summary`.

**Breaking, requires a version change:** removing or renaming a frame or a field; changing the
type of an existing field; making an optional field required; changing what an existing `verb` or
`error.code` means.

Two rules are load-bearing enough to call out separately, because breaking either produces a
client that looks fine against a stale leaf and fails against a current one:

- **`token` is opaque.** No client parses, inspects or reconstructs it.
- **`id` on `command.proposed` and on the tool frames is a correlation handle**, meaningful only
  within its own turn. Nothing persists it or treats it as a resource identifier.
