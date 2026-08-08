# The assistant wire contract

**Contract version 2.0.**

The public HTTP contract between the assistant leaf and any browser client. Two clients consume
it — `kgsm-web`'s assistant dock and the standalone assistant SPA — on independent deploy
cadences, so the shapes here are the compatibility boundary: additive changes are free, anything
else is a version bump.

This document covers the four channels that carry an assistant interaction: the **turn stream**
(`POST /turn`), the **confirm channel** (`POST /confirm`), the **command surface**
(`GET /commands`, `POST /commands/{name}`), and the **conversation event stream** (`GET /events`).
Authentication, conversations and the review surface are separate; see `CONFIGURATION.md` and
`ARCHITECTURE.md`.

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
| `tools` | restrict the offered tool set |
| `conversationId` | partitions this user's own history into separate context windows. It carries no identity — memory is always keyed by the server-resolved user id |

**Thinking and auto-run are not turn fields.** They are switches the *conversation* carries, set by
the `/think` and `/autorun` commands (§4) and read by the leaf when the turn runs — so two surfaces
looking at one conversation cannot disagree about what it is set to, and a client cannot ask for
behaviour the stored preference contradicts. A switch nothing has set falls to the host's configured
default, never to `false`.

Both are **read back**: `GET /conversations` carries `think` and `autorun` on every row, and
`GET /conversations/{id}` carries them for the one — already resolved against that default, so what a
client shows is what the next turn will run on. A surface displaying the switches states what it read,
never what it last remembered, since any other surface may have moved them since. A move made while
it is watching arrives on the event stream (§5), so it need not wait to be asked.

Auto-run is **intent, not authority**. It asks for lifecycle commands to run with no confirmation
step, and every gate it passes narrows it further: the caller's admin tier, and on the relay path the
surface's own `X-Relay-Auto-Act` floor. It does **not** gate whether an action may be *proposed* —
that follows the caller's operator tier alone, because the user confirms every proposal.

### Frames

| Frame | Payload | Meaning |
|---|---|---|
| `text.delta` | `{ text }` | one slice of the reply |
| `thinking.delta` | `{ text }` | one slice of model reasoning; emitted only when the conversation's thinking switch is on |
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
| `token` | the opaque, security-bearing handle onto the staged operation. This is what `POST /confirm` takes |
| `configKey` / `configValue` | `set_config` (the key and value) and `open_ports` (the port spec, and `router` on the key when a UPnP forward is included) |
| `instanceName` | the custom name for an `install`; absent when kgsm auto-names |
| `file` | `write_file` only: `{ path, proposedContent }` — the complete new content, so a client can render a diff before the user confirms |

`token` and `file` are independent by design: the handle identifies the staged operation, and the
frame carries the content a client needs to show. The operation itself, file body included, is held
by the Service — the handle is 32 characters whatever the operation is, so it fits inside every
surface's identifier limits, a Discord button's 100-character id included.

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
| `confirm_failed` | confirm channel |

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

Authority is **re-derived at confirm time**, never read off the token. A handle is single-use, and
one staged by a different user is refused with the same message as an unknown, already-redeemed or
expired one, so the response is not an oracle for which case occurred. A refused handle is not
consumed: someone else's guess cannot cancel an action its owner is about to approve.

### Buffered form

Every confirmation kind answers `{ text, success, card?, confirmations?, outcome? }`.

- `text` — the outcome, human-readable.
- `success` — whether the operation may be presented as having succeeded. True only when the
  postcondition was **observed**, or when the engine reported success for a verb that has none.
- `card` — the rich outcome card, on the kinds that have one.
- `confirmations` — a fresh confirmation token when the outcome leaves something to confirm
  again. A blueprint finalize whose repair loop exhausts returns its draft this way, which is the
  re-edit loop.
- `outcome` — what is actually known, below.

### The outcome, and why `success` is not enough

`kgsm lifecycle start` returns as soon as the spawn is accepted. That answers "was the request
taken", which is a different question from "is the server running", and reporting the first as the
second is how a client comes to tell someone their server is up when it is not. A confirmed
lifecycle command is therefore **watched** until it reaches its run-state postcondition, and the
answer says which of those happened:

```json
{ "verdict": "settled", "verb": "start", "instance": "factorio", "observedState": "running" }
```

| `verdict` | Meaning | `success` |
|---|---|---|
| `settled` | Ran, and the run-state postcondition was observed | `true` |
| `accepted` | Ran, the engine reported success, and the verb has no run-state postcondition (an update, a backup, a config write) | `true` |
| `notSettled` | Ran, but the end state was not reached before the window closed. `observedState` says what was actually seen | `false` |
| `unknown` | Ran, and the end state could not be read. Never reported as "stopped" | `false` |
| `failed` | The operation itself reported failure | `false` |
| `refused` | Nothing ran — not authorized, target gone, or the staged payload was unusable | `false` |

`observedState` is `running`, `stopped`, or `unknown`, and is present only for the verbs that have
a run-state postcondition (`start`, `stop`, `restart`). `unknown` means the read failed; it is
never a stand-in for "not running". `reason` carries why a read failed or an operation failed.

What is observed is the **engine's run state** — the process is up — not that the game inside it is
ready for players. `settled` claims the former and nothing more.

A client renders the verdict. Parsing `text` to work out which case occurred is how two surfaces
come to disagree, and `text` is the one field here that is free to be reworded.

### Streamed form

**Every confirmation kind streams**, because any of them can be slow and silent: a blueprint
finalize runs a minutes-long test-install → boot → verify → bounded-repair pipeline, an install
downloads a game, and a lifecycle command is watched until it reaches its run state. Buffered into
one response, that silence is what an idle-connection reaper on a remote path drops, leaving the
caller's card spinning with no terminal result.

| Frame | Meaning |
|---|---|
| `progress` | a step of the work, same shape as the turn stream's |
| `: keepalive` | a comment every 15s, so no idle reaper drops the socket |
| `result` | terminal — carries the whole buffered response object |
| `error` | terminal failure after the status committed |

`result` is the confirm channel's terminal frame and is distinct from the turn stream's `done`: a
confirm's outcome is a verdict or a card, not assembled text.

Which steps appear depends on the work. A blueprint finalize narrates its pipeline. A lifecycle
command narrates `settling` — *"Waiting for factorio to come up…"* — and only once it is actually
waiting, since the ordinary case reaches its run state on the first read and has no wait to report.
A kind that reports no steps still gets heartbeats and a terminal frame.

Token, authority and the staged payload are all resolved **before** the stream opens, so a stale
token is a plain JSON 4xx and never a 200 whose only failure signal is buried in a frame.

**A streamed turn is a shared session, not this request's work.** The response is its first consumer;
other surfaces attach over `GET /events` (§5) and receive the very same frames. The caller going away
does not end it, and `DELETE /turns/{turnId}` from any of that person's surfaces does. A **buffered**
caller — no `Accept: text/event-stream` — runs outside that model: one whole answer, not attachable,
not stoppable from elsewhere, not queued.

---

## 4 · The command surface — `GET /commands`, `POST /commands/{name}`

The commands a person can type at the assistant. **The leaf runs every command it lists**, so a
client treats the catalog as authoritative rather than advisory: a name that appears in `GET
/commands` is a name `POST /commands/{name}` will honour.

`GET /commands` answers the catalog **filtered to the caller's tier** — a command above it is absent,
never listed and then refused. Each entry carries `name`, `description`, `mutates`, and `options`;
an option carries `values` when it offers a fixed set rather than free text.

`POST /commands/{name}` takes `{ conversationId?, argument? }` and answers a result whose `message`
is always present — the one line a surface puts in the transcript — plus whichever of these the
command produced:

| Field | From |
|---|---|
| `conversationId` | `/new` — the conversation that now stands |
| `state` | `/think`, `/autorun` — the state the switch now stands at |
| `compaction` | `/compact` — `{ compacted, messagesCompacted, summary }` |
| `commands` | `/help` — the same catalog the listing answers |
| `tools` | `/tools` — the same tools `GET /tools` answers |

A switch given no `argument` **toggles**; `on`/`off` names a state. Anything else is a 400 rather
than a guess. An unknown command name is a 404 — this endpoint is not a second way to ask a
question, so a client's typo surfaces as one instead of reaching the model.

**`/new` answers which conversation now stands, and a client follows it there.** The offered
`conversationId` is taken up only while it holds nothing — a surface that minted an id and is asking
for it to be brought into being. Sent from a conversation that has been spoken in, the leaf starts a
different one and names it, because "start a fresh conversation" cannot mean the one it was typed in.
Either way the answer is the conversation the next turn should carry.

The gate is re-checked at the POST rather than trusted from the listing: a client can post any name,
and the listing is a convenience, never the authorization.

The same catalog ships as `/var/lib/kgsm/leaves/commands/assistant.json` for the Control Panel, in
the format `leaf-command-manifest.md` owns. The file is the **whole** catalog where the endpoint is
filtered: a live surface shows a person what they can type, a descriptive file documents the leaf.

---

## 5 · The conversation event stream — `GET /events`

A conversation can be open in more than one place at once — the Control Panel in a tab, the
installed assistant app on a phone, a Discord thread — and it carries state those surfaces show:
which switches it stands at, whether it exists, what its log holds. This stream is how they find out
without asking each other.

`Accept: text/event-stream`, a session bearer, and the stream stays open. It is **principal-scoped**:
it carries the caller's own conversations and nothing else, resolved from the bearer the same way
every other read is.

The first frame names the stream:

```
event: hello
data: {"type":"hello","streamId":"3c8075be2cb94f1482dc473810db9a43"}
```

A client sends that id back as **`X-Assistant-Origin`** on the calls it makes. The events those calls
cause come back carrying it as `origin`, so a surface can tell its own echo from a change made
somewhere else and decline to apply what it has already applied. The header is optional everywhere;
a caller that sends none is simply not distinguished, which costs it one redundant re-read.

| Frame | Payload | Meaning |
|---|---|---|
| `conversation.switches` | `{ conversationId, origin, think, autorun }` | where the switches now stand — **effective**, resolved as the listing resolves them, so applying the frame lands exactly where a re-read would |
| `conversation.started` | `{ conversationId, origin }` | a conversation exists and is listable |
| `conversation.deleted` | `{ conversationId, origin }` | a conversation was soft-deleted and should leave the list |
| `conversation.activity` | `{ conversationId, origin }` | its log grew — a turn, or a compaction checkpoint |
| `turn.attach` | the whole state of a turn (below) | a turn started, or this stream is being redrawn |
| `turn.queue` | `{ conversationId, runningTurnId, queued: [{turnId, prompt}] }` | what is running here and what waits behind it |

### Turns are shared, and a stream attaches to one conversation

A turn runs at the leaf with its own lifetime, and every one of that person's surfaces can attach to
it — so the same turn is watched from more than one place, and any of them can end it.

**Turn frames go only to the streams attached to that conversation.** A stream says which one with:

```
POST /events/attach   { conversationId }      → 204
```

using its `X-Assistant-Origin` id to name itself. The state frames above keep going to **every**
stream, because they are about the chat list rather than about one conversation. Attaching answers
**on the stream** — `turn.attach` if something is running there, `turn.queue` naming no running turn
if not — never in the response body, so a surface renders from one source.

`turn.attach` carries everything that happened before this consumer arrived, and is also what a
consumer that fell behind is redrawn with rather than being fed deltas with a hole in them:

```json
{ "turnId": "t_…", "conversationId": "…", "prompt": "…", "state": "running",
  "text": "the reply so far", "thinking": null,
  "tools": [{ "id": "…", "name": "get_status", "state": "done", "summary": "…", "card": null }],
  "proposals": [ … ], "queued": [ … ], "done": null, "error": null }
```

After it, frames are the **verbatim** §2 vocabulary. A watcher's experience is not a reduced version
of the sender's, and the frames are produced once at the leaf, so two surfaces cannot be shown
different renderings of one turn.

**`POST /turn` with `Accept: text/event-stream` is itself an attach** — its response is the session's
first consumer and receives exactly those frames. Leaving detaches it; the turn keeps running.

```
DELETE /turns/{turnId}    → 204 stopped or cancelled, 404 unknown or not yours
```

Stop is a call rather than a disconnect, because a surface that is only watching holds no connection
to abort. Idempotent, and authoritative for everyone. It ends **that** turn; anything queued behind it
proceeds, and discarding one of those is the same call on its own id.

A conversation runs **one turn at a time**. A second prompt queues, up to three; a fourth is
`409 queue_full`. A queued turn re-derives its authority when it runs, never at enqueue — a role
removed while it waited takes effect on it. The queue is in memory and dies with the leaf.

A stopped turn is recorded with the text it had already streamed, `outcome: "cancelled"` — so a
surface that watched it and one that reads it back afterwards describe it the same way.

**Only the switches travel by value.** Everything else names a conversation and stops there: a
transcript has one way to be obtained, and a second streaming path for it could drift from the first.
A client answers `activity` by re-reading the conversation, and `started` by re-reading the listing.

**Nothing is buffered for a stream that is not connected**, and there is no replay, no cursor and no
delivery guarantee — a client that reconnects **re-reads the listing**, which restates every
conversation's switches in one call and closes whatever gap the outage left. That is what lets the
stream be a plain optimisation: a surface with no stream at all is correct, just slower to notice.

A comment frame (`: ping`) every 20 seconds keeps the connection through a proxy's idle timeout, and
is the beat on which the caller's session is **re-checked** — a stream held open does not outlive the
logout that was meant to end it.

---

## 6 · Compatibility

The contract version at the top of this document changes when a client must change with it.

**Additive, no version change:** a new frame type; a new optional field; a new `verb`; a new
`error.code`; a card lit on a tool that had none; a new `data` shape under an existing card. All
of these are safe because clients ignore what they do not recognise and fall back to `summary`.

**Breaking, requires a version change:** removing or renaming a frame or a field; changing the
type of an existing field; making an optional field required; changing what an existing `verb` or
`error.code` means.

Two rules are load-bearing enough to call out separately, because breaking either produces a
client that looks fine against a stale leaf and fails against a current one:

- **`token` is opaque.** No client parses, inspects or reconstructs it. It is a handle the Service
  issued, not an encoding of the operation, and only the Service can say what it means.
- **`id` on `command.proposed` and on the tool frames is a correlation handle**, meaningful only
  within its own turn. Nothing persists it or treats it as a resource identifier.
