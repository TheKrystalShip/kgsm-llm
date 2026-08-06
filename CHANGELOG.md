# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — the callback carries Discord's own refusal back

`/auth/discord/callback` collapsed every code-less callback into `bad_request`, which is also what a
malformed one returns. It now reads Discord's `error` parameter and carries it through verbatim
(`consent_required`, `login_required`, …), so a client can tell "this sign-in needs a human" from
"something broke" and retry visibly instead of guessing. Reported only after the state check — an
error response carries `state` too, and a forged one must not be echoed back.

This is what lets a browser attempt a **silent** sign-in: every surface on a host is the same
Discord application, so a browser that authorized the app for the Control Panel has already
authorized it here, and `prompt=none` completes with nothing rendered.

### Fixed — a direct caller may propose actions without the auto-run toggle

Proposing an action and auto-running one are separate permissions, and the direct session-bearer
path had collapsed them into the request's `actions` flag. An admin talking to the assistant from
the Control Panel was told it could not start their server unless auto-run happened to be on — while
the same person, same authority, reaching the same assistant through the relay could.

- `canPerform` (may propose) now follows the caller's own operator tier alone. The user confirms
  every proposal, so gating that on a toggle only hides the button.
- `actions` means what the panel's switch has always said it means: **auto-run**, ANDed with the
  caller's admin tier so it can only ever narrow.
- Both transports resolve the two from the same ladder. A caller's capability follows their
  authority, never the transport that carried the turn.

### Added — a browser client can be signed in and returned to itself

The callback answered JSON: `{status, tier, accessToken, refreshToken, …}`. Correct for a
programmatic caller and useless for a browser, which lands on a page of raw JSON — so a client on
another origin had no way to complete a sign-in against this service at all.

`/auth/discord/start` now takes `return_to`, and a login that carries one comes back as a `302` to
that address with the outcome in the URL **fragment** — `#access=…&refresh=…&tier=…`, or
`#error=<code>`. The fragment rather than the query because a fragment is never sent to a server,
kept in a `Referer`, or written to an access log. The key names are the ones kgsm-api already hands
back, so a client reads either with the same code. Without `return_to` the JSON contract is
unchanged. Errors take the same route as successes: a browser that asked to be returned is never
dead-ended on a body it cannot act on.

**A redirect target that arrives on a request is an open-redirect surface, and this one hands over a
real session**, so it is checked against `Auth:AllowedOrigins` at both ends. At `/start`, because
bouncing to Discord for a login that cannot complete spends the user's consent on a dead end. At the
callback, because the cookie carrying the address between the two is client-held and carries no
integrity of its own — without the second check, setting one cookie by hand is enough.

`AllowedOrigins` does both jobs deliberately: a client trusted to call with a bearer is exactly a
client trusted to be handed one, and two lists would drift.

`DELETE` joins the CORS methods. A client owns its conversations and removing one is a `DELETE`;
without it a cross-origin client accumulates a history it has no way to clear.

### Changed — every confirmation streams, not just a blueprint finalize

Streaming existed only for a blueprint finalize, because that was the one confirmation known to run
for minutes. The others are not fast: an install downloads a game, and a lifecycle command is now
watched until it reaches its run state. Buffered into one response, each is a long silence on one
socket — the thing an idle-connection reaper on a remote path drops, leaving the caller's card
spinning with no terminal result. `Accept: text/event-stream` now streams **any** kind: progress
steps, a keep-alive comment every 15s, and a terminal `result` frame carrying the same
`ConfirmResponse` a buffered caller gets. Without the header the buffered contract is unchanged.

`SseConfirmWriter` no longer knows what it is running — it takes the work as a function and owns
only the streaming mechanics, so every kind reaches the wire through one path. That also collapsed
the blueprint response, which was built once for the buffered path and again inside the writer: two
copies of the re-edit token and card logic, with nothing keeping them in step.

A settling lifecycle command narrates `settling` — *"Waiting for factorio to come up…"* — reported
only once the wait is real, since the ordinary case reaches its run state on the first read and has
no wait to report.

The confirm channel's in-band error code is `confirm_failed`; it was named for the finalize that
used to be the only thing streaming.

### Changed — a confirmed command reports what was observed, not what was accepted

A confirmed lifecycle command answered from the engine's exit code: *"'factorio' has been started."*
That exit code carries whatever the spawn path checked and nothing further, and the confirm path
restated it as a claim about the server. The watchdog does reject a spawn that fails outright — a
start whose binary cannot exec comes back as a real failure, measured — so the gap is not every
failed start. It is every way a start can be accepted and still not arrive: a process that survives
the spawn and dies later, an instance that hangs part-way up, a container whose run state is a
different source, a stop the game outlives while it saves.

A confirmed `start` / `stop` / `restart` is now **watched** until the run state reaches its
postcondition, and the answer says which of those happened: `settled` (observed), `accepted` (the
engine reported success and the verb has no run-state postcondition — an update, a backup, a config
write), `notSettled` (ran, never got there), `unknown` (ran, the state could not be read), `failed`,
or `refused`. `notSettled` and `unknown` are **not** successes, and the two are kept apart: "we
looked and it wasn't running" and "we could not look" are different facts, and neither may collapse
into the other.

The observation source is the same measured-or-unavailable fleet read the status card is built from,
so the confirm path and the status card cannot disagree about what a server is doing. A per-instance
liveness check was available and rejected: it answers with a bare boolean, which cannot tell "not
running" from "could not read" — exactly the distinction being preserved. What is observed is the
engine's run state, the process being up, not the game inside it being ready for players.

The window is 90 seconds at 1-second reads, and it is a ceiling rather than an expectation — the
common case settles on the first read, with no delay. It is a registered `SettlementTiming`, so a
host can substitute one.

`IServerAssistant.ConfirmAsync` returns a `ConfirmOutcome` instead of a `Result<string>`; `/confirm`
carries it as an additive `outcome` object, and `success` is now true only for an observed or
accepted outcome. The CLI grew a third rendering — an unsettled confirmation is neither a tick nor a
cross.

**The auto-run path had the identical defect and is fixed with it**, where it mattered more: that
string is what the model reads, and a model told *"Done — it has been started"* tells the user the
server is up. It now reports the verdict, so an unsettled or unreadable auto-run is described as
such.

### Added — a versioned public wire contract

`docs/wire-contract.md` is the compatibility boundary between this leaf and the browser clients
that consume it. It covers both channels an interaction runs over — the `/turn` stream and the
`/confirm` channel — their frames, the confirmation envelope, the terminal-result shape, the error
envelope, and the rules a client may rely on: absent optional fields are omitted rather than
`null`, unknown frames and fields are ignored, `token` is opaque, and a correlation `id` means
nothing outside its own turn. It names what is additive and what is breaking, so a leaf deploy can
land ahead of either client.

It replaces a spec written as a migration checklist against another repo's milestone, which had
gone stale: it still described one tool carrying a structured result card, where fourteen call
sites now do. The contract states the rule instead — a card appears when the tool has a structured
source, a client keys on `result.tool` and falls back to `summary` for a tool it does not know —
so lighting a card on a new tool needs no client change and no doc edit.

### Fixed
- A session row recorded the **raw** `Auth:HostId` while its tokens were minted under the **resolved**
  one, so with the shipped blank default the row claimed host `""` and the token said `hotrod` — one
  host identity with two spellings. Nothing read the column yet, which is why it took a real login to
  surface it. Both now come from `ResolveHostId()`.

### Added — the assistant is reachable on its own hostname

`deploy/nginx/kgsm-assistant.conf` is this leaf's own nginx server block, installed into
`/etc/nginx/conf.d/` by `deploy/setup.sh` when the host runs nginx and skipped cleanly when it does
not. The service still binds loopback only; nginx terminates TLS and routes by hostname.

This is what makes the leaf's Discord login mean something: it can now authenticate a browser with
no Control Panel API in front of it, which is the standalone property that login was built for.
`DiscordOAuth__RedirectUri` points at **this service's own** `/auth/discord/callback`.

`proxy_read_timeout 3600s` is load-bearing — a blueprint finalize streams for minutes behind
heartbeats, and nginx's 60s default would cut it mid-run.

### Changed — the relay forwards one tier instead of two booleans

- **`X-Relay-Tier` replaces `X-Relay-Can-Act` and `X-Relay-Admin`.** A relayed caller's authority is
  whatever the api verified — this service does no Discord lookup for one, since a relay host may have
  no Discord configuration of its own — so it is now one value answering every authority question
  instead of two booleans that could disagree.
- **The parse is fail-closed:** an unrecognised spelling, an empty header, or no header at all is
  `None`. A relay that says nothing is never read as saying yes, on either the action or the review
  surface.
- `X-Relay-Auto-Act` is unchanged — admin tier **∧** the user's per-turn toggle is a preference riding
  a permission, not a permission.

### Changed — the whole sign-in runs on the shared auth pipeline

The service now uses `TheKrystalShip.KGSM.Auth.Discord` and `.Auth.Sessions` for everything it used
to hand-roll: one chokepoint to `discord.com`, and session tokens minted and validated by the same
code the Control Panel API runs.

- **The login handshake is bound to the browser that started it.** The CSRF `state` and the PKCE
  verifier ride together in one HttpOnly cookie instead of a server-wide dictionary. Checking a
  returned state against a set of issued states proves only that *some* login started on this host —
  which is true of an attacker's own login too, so it admitted exactly the request it was meant to
  refuse. Single-use consumption stops replay, not CSRF.
- **Sessions are JWTs backed by a SQLite registry**, in the same file as the conversation history.
  A restart no longer signs everyone out, `POST /auth/logout` kills a session within the validator's
  5-second cache rather than leaving the bearer valid until it expires, and a refresh token is
  single-use with reuse detection.
- **New sign-in endpoints:** `GET /auth/discord/start` (302 to Discord) and
  `GET /auth/discord/callback` replace `GET /auth/login` + `POST /auth/callback`, and
  `POST /auth/session/refresh` trades a refresh token for a fresh pair.
- **`Auth:SigningKey` must be set on a real host.** Unset generates a per-process key, so every
  restart invalidates every issued token. `Auth:AccessTtlSeconds` (15 min) and `Auth:SessionTtlSeconds`
  (30 days, sliding) replace the single one-hour session lifetime.
- `GET /auth/me` reports the caller's live `tier` alongside `canPerformActions`.
- **A Discord outage denies an authority check without being cached as a denial.** "We could not ask"
  is not "the answer is no"; caching it would turn a brief outage into a full role-cache-TTL lockout
  for someone who really is an operator.

### Changed — authority is the ecosystem's tier

- **The Discord app, guild, role-lookup token and role map move to the shared `KgsmAuth` section**
  (`/etc/kgsm/discord-auth.env`, owned by `TheKrystalShip.KGSM.Auth`), so this service, the Control
  Panel API and the Discord bot resolve the same authority for the same person.
- **`DiscordOAuth:ActionRoleId` and `DiscordOAuth:AdminRoleId` are replaced by the ordered tier.**
  Acting needs `operator`; reading another person's conversations needs `admin`. The review power is
  the administrator's, not a role of its own.
- **The role cache is keyed by user, not by (role, user).** One tier answers every authority question
  the service asks, so there is nothing left for a per-role key to keep apart.
- `DiscordOAuth` keeps only this surface's own `RedirectUri` and `Scopes`.

### Added
- Turn feedback: an answer's reader can mark it helpful or unhelpful, with an optional note on a
  thumbs-down. Stored as a `feedback` entry in the same append-only log (latest-wins, so re-rating and
  un-rating are appends), never as an edit to the turn it judges, and never replayed into the model's
  context. `POST /conversations/{id}/turns/{turnId}/feedback` is principal-scoped, and the store
  additionally verifies the turn belongs to the named conversation — entry ids are log-wide, so the
  route alone would let a caller reach a neighbouring id.
- Turns are addressable: `AppendTurn` returns the entry id, which rides `AgentEvent.Final` out to the
  SSE `done` frame and appears on every history entry. A surface can now act on a specific answer
  instead of inferring which turn was the last one.
- `GET /admin/conversations/stats` reports rated/positive/negative turn counts, a satisfaction rate
  (null until something is rated), per-prompt-version verdict counts, and the notes people wrote.

### Fixed
- Conversation recency and soft-delete resolution ignore feedback entries, so rating an old chat no
  longer reorders its owner's history and rating a turn in a hidden chat no longer un-hides it.


### Added — the corpus roll-up behind the assistant's operator overview

`GET /admin/conversations/stats` (admin-gated, like the rest of the review surface) answers "how is
this assistant doing" from the same append-only log the transcripts come from, so a figure can never
disagree with the turns behind it: the outcome mix, the answer-time distribution, per-tool call
counts / durations / failures, system-prompt-version buckets, context occupancy, and turns per day.

- **`IConversationStore.GetStats(surface)`** derives all of it on demand — conversation shape and
  per-turn scalars via `json_extract`, the tool trajectory exploded with `json_each`, percentiles
  finished in memory (SQLite has no percentile aggregate, and the ordered lists are needed anyway).
  Soft-deleted conversations are **included**: their turns are part of what the assistant did, and
  dropping them would understate the corpus a review is judging.
- **A count is zero; an unmeasured distribution is null.** A corpus with nothing timed reports a null
  median rather than a `0` that would render as "instant" — a fabricated measurement.
  `ContextWindow` is likewise null when the corpus spans more than one window, because one percentage
  over two denominators describes neither.
- **A tool the catalog does not define is reported, not dropped.** The store is domain-blind and
  hands back the name it recorded; the Service marks it `known: false`. A model calling a tool that
  has never existed is the sharpest tuning signal the corpus carries. The check is against the new
  `LlmTools.EveryToolName` — the full catalog including tools offered only in context
  (`revise_blueprint`, appended when a draft is open) — rather than the ordinary-turn offer, which
  would report a real conditional tool as invented.
- The response carries the **live runtime** (model, context window, iteration cap, actions on/off)
  beside the numbers: "median 2 tool steps" only means something next to "the cap is 16".

### Added — an administrator can read other users' conversations, to tune the assistant

The conversation log already holds everything a review needs — the prompt, the reply, the thinking,
the full tool trajectory, iterations, usage and outcome of every turn. What it lacked was a way to
ask *who* has talked to the assistant and to read one of their conversations. Both are now first
class, and nothing new is captured to provide them.

- **`IConversationStore.ListActors(surface)`** — one row per `{surface}:{user}` namespace, derived
  from the conversation ids themselves rather than from a user table the store would then have to
  keep true. It answers "whose conversations exist", the reverse of `ListConversations`, which needs
  a scope key the caller already knows.
- **`ListConversations(scope, includeDeleted)`** — a review sees conversations their owner hid. The
  transcript was never erased (the log is append-only; a soft-delete is a tombstone), and a hidden
  conversation is exactly the one a tuning review wants.
- **A conversation summary carries the signal a reviewer scans for**: `ErrorTurns` and `CapHitTurns`
  — turns that failed, or that exhausted the iteration cap without answering. Both are derived in SQL
  from the stored turn payload, so no column, no migration, and every conversation already in the
  log reports them.
- **`ConversationTurnRecord.UserDisplay`** — the asking user's display name, supplied by the host
  through `AgentTurn` and recorded on the turn. The conversation id carries only an opaque user
  segment (a Discord snowflake), so this is the only place a human-readable name exists. Turns
  recorded before it have none, and read back as `null`: a reader shows the raw id rather than a name
  inferred from it.
- **`GET /admin/conversations/users`, `?user=`, and `/{id}`** on the Service, behind a new
  `AdminOnlyFilter`. A transcript is addressed by an **opaque handle minted by the listing**, not by
  a key the client composes, and the handle is refused if it decodes outside the web surface — the
  surface only ever serves what it listed. The entries come back in the **same shape as your own
  history**, so a client renders a reviewed transcript through its existing path.
- **Authority, both ways in.** Over the trusted relay it is the api's verified decision, forwarded as
  `X-Relay-Admin` and fail-closed exactly like `X-Relay-Can-Act` — an api that does not speak the
  header cannot open the surface by omission. For a direct session bearer it is the caller's own
  Discord role, `DiscordOAuth:AdminRoleId`, so the leaf's review surface works with no api in front
  of it. It is deliberately **not** the action role: acting on a server and reading someone's
  conversation are different powers. Unset ⇒ no session bearer may review.
- `RoleCache` keys its entries by **role and user**. The service asks about two roles now, and one
  slot per user would let "may act" answer "may review".

There is no admin write: no editing, deleting or compacting another user's conversation.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-llm.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `the bound settings types`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

- **Two descriptor defaults were wrong and are now read from the settings file.** `bindAddress`
  published `http://127.0.0.1:5180` while the file declares `http://localhost:5180` — the deployed
  value passed off as the coded one, on the screen whose whole job is saying where a value came from.
  `ragMinScore` published `0` against the file's `0.0`. The `Urls` configuration key and the
  `ASPNETCORE_URLS` variable are one setting reached two ways, which the field now says outright.
- **The `Rag` section's fields render in a slightly different order.** It is bound from three types in
  three assemblies, and fields follow their declaring type; no key, default or bound changed.
- **`SearchOptions.LocalEnabled`/`WebEnabled`/`Available` are marked `[LeafIgnore]`.** They are
  computed at composition, not configuration, and their doc comments already said so — now the build
  agrees.

### Changed — one settings file, and it declares the whole surface

- **`appsettings.json` is now `kgsm-assistant.settings.json`**, matching the ecosystem's
  `kgsm-<leaf>.settings.json` naming, and `Program.cs` loads it by absolute path from the binary's
  own directory rather than relying on the content root. It is inserted at the **front** of the
  configuration sources, because it is the floor: the unit's `Environment=`,
  `/etc/kgsm-assistant/service.env` and a command-line argument all still override one key of it.
  (The CLI and Eval are interactive tools, not leaves, and keep `appsettings.json`.)
- **Eleven settings the service binds but the file never declared are now declared**, with the value
  the code already used: `Ollama__Temperature`/`Seed`/`Think`, `LlmAgent__MaxToolOutputChars`/
  `IterationLimitReply`, `Monitor__SocketPath`, `Watchdog__SocketPath`, `Firewall__SocketPath`,
  `KGSM__JournalDir`, and the two RAG task prefixes. Reading the file now tells you the whole
  surface. `Rag__DocumentPrefix`/`QueryPrefix` are declared **null**, not `""` — null resolves the
  prefix from the embedding model's name, while an empty string is a real prefix meaning "none".
- **`Prompts__Directory` is declared and described.** The prompt-override directory outranks every
  other way of setting the assistant's system prompt, it is set on this host, and it was in no
  settings file, no descriptor and no env template — so the Control Panel could not show it.

### Fixed
- **`deploy/assistant.env.example` no longer documents `KGSM__EventSocketPath`**, which nothing has
  read since the engine moved to the event journal. It named the journal directory instead.
- **`floorSources` lists the settings file first.** The list is lowest-precedence-first, so with the
  file listed last the Control Panel resolved a knob to the file's value and reported it as the
  deployed one — showing a blank where the unit sets a real path.

### Added
- **Four tests hold the settings file, the bound options classes and the leaf descriptor together**:
  a key in the file that binds to nothing, a bound setting the file never declares, a descriptor
  default that disagrees with the file, and an env template naming a key the file does not declare
  each fail the build.

### Changed
- **`pairedApiKey` names the Control Panel API's renamed setting.** kgsm-api's environment
  variables are now spelled `Api__<Property>`, and this value is what the API resolves to warn that
  a change here has moved this leaf out of its reach. Naming the old key would have made that check
  silently find nothing and report the change as clean.

### Fixed (v1.29.2) — the config panel no longer claims a journal default the service does not have

- **`kgsmJournalDir` declares no `default` in the leaf descriptor**, because the service genuinely
  has none: `KgsmConnectionOptions.JournalDir` is empty unless configured, and empty means this host
  reads no events at all. The descriptor had named `/var/lib/kgsm/events` as the coded default,
  which the panel renders as the fallback provenance tier — so clearing the override read as
  "falls back to the standard journal" when it actually turns event reading off and leaves the
  blueprint cache on its TTL. The standard location moves into the description, where it informs
  without claiming to be what the code does.

  The descriptor coverage test does not catch this class of error: it checks that `enum`, `int` and
  `bool` defaults are well-formed and in range, but a `path` default is a free string it cannot
  compare against the source.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Changed (v1.29.0) — engine events come from the journal, not a socket

- **`KGSM__JournalDir` replaces `KGSM__EventSocketPath`.** The service tails the engine's
  append-only event journal instead of binding a socket for the engine to deliver to, so it claims
  no path and the engine needs no configuration naming this consumer. The blueprint-cache
  invalidation handlers are unchanged. It reads from the tail and keeps no position: this listener
  exists only to drop a cache, and replaying history would re-invalidate for edits already
  reflected in what the next read returns.

  **The reason the listener stays opt-in has changed.** It used to be a hard constraint — socket
  binding is exclusive, so a one-shot CLI would have stolen the resident service's socket. A
  journal is a file: any number of readers coexist, so a CLI run beside the service would now be
  harmless. It remains opt-in purely because a one-shot invocation has no cache worth keeping warm.

### Changed (v1.28.0) — the server note is not editable from chat

- **`set_config_value` refuses the server note's keys** (`note`, `note_updated_by`, `note_updated_at`)
  and tells the user to edit it on the control panel's server page. kgsm accepts them as ordinary
  runtime values, so nothing downstream would have stopped a chat turn from rewriting a player-facing
  note — raw and unencoded into a file that is sourced as `key="value"`, credited to nobody. The note
  has one surface that owns its encoding and records who wrote it, and this keeps it that way. The
  refusal happens at staging, so the user hears it immediately rather than after clicking Confirm.

### Added (v1.27.0) — the Control Panel can configure this service

- **`deploy/kgsm-llm.leaf.json` declares every setting the service binds** — all 64 of them, from the
  model and the agent loop through Discord sign-in, web search and the knowledge base, grouped into
  13 sections with each setting's type, coded default, bounds, unit and risk. `deploy.sh` installs it
  into `/var/lib/kgsm/leaves/` under the leaf id **`assistant`** (this repo is `kgsm-llm`, but the
  leaf kgsm-api knows is named for what it does), and the Control Panel renders the configuration
  page from it. Nothing in kgsm-api needs to know about this service for that to work.
- **A coverage test fails the build if the descriptor and the code disagree.** It walks the options
  types the service actually binds, so a property added to a bound options class is caught the moment
  it exists, and a descriptor entry naming a setting nothing binds is caught too — an override written
  for one would be reported as applied while changing nothing.
- The six secrets stay write-only: a read reports only that a value exists, never the value, and
  never a default. The relay secret names the API setting it must match; the listen address names the
  API's view of it.
- **Four list settings are deliberately not on the panel** — `Auth:AllowedOrigins`,
  `WebFetch:AllowedHosts`, `WebFetch:DeniedHosts` and `Rag:Sources`. A list binds from indexed keys,
  which one environment variable cannot express, so declaring them would promise an edit the panel
  could not deliver. They stay a file edit, and the coverage test pins the exclusion so it cannot
  grow silently.

### Added (v1.26.0) — the assistant listens to the engine's events

- **`kgsm-assistant-service` binds its own kgsm event socket
  (`/run/kgsm-assistant/events.sock`) and drops its blueprint cache the moment a blueprint is
  written.** A blueprint edited anywhere else on the host — the Control Panel's library editor,
  another operator's CLI — used to be invisible here until the catalog TTL expired, so the assistant
  answered from a stale snapshot. It now invalidates on the engine's own
  `blueprint_created`/`_updated`/`_removed`, so the next turn reads the new values.
- **The listener is opt-in per host, via the new `KGSM:EventSocketPath`
  (`KGSM__EventSocketPath`).** Binding a unix socket is exclusive, so only a resident host may
  listen: `AddKgsmEventListener()` registers nothing when the path is unset, which is what keeps a
  one-shot `kgsm-assistant-cli` run from taking the socket away from the service. The service's unit
  sets the path and gets `RuntimeDirectory=kgsm-assistant`; kgsm delivers there only when the same
  path is listed in its `event_socket_filenames` (it ships in the engine's default list).
- Events only make an existing cache fresher — nothing gates a read on them, so the assistant still
  runs standalone with no socket, on the TTL alone. The 8 manual `_invalidation.Invalidate()` calls
  in `BlueprintAuthoringAggregator` and the `POST /events` webhook both remain as independent
  freshness paths.
- **kgsm-lib 1.42.0 → 1.45.0** for the blueprint event types. The bump also moves
  `IBlueprintFiles` onto a constructor that needs `IBlueprintService` + `IEventManagementService`:
  both are now registered, and `BlueprintFilesWiringTests` asserts the write authority resolves, a
  break that is otherwise invisible until the first blueprint write.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-assistant` to the deploying user, seeds `/etc/kgsm-assistant/service.env`, creates the
  `service`/`cli`/`indexer`/`docs` tree plus `/var/lib/kgsm-assistant` and the
  `/usr/local/bin/kgsm-assistant-cli` symlink, puts the real units in `/etc/kgsm-assistant/systemd/`
  with the `/etc/systemd/system/` entries symlinked to them, installs a polkit grant scoped to this
  project's units, and verifies the grant with the same unprivileged `systemctl` calls `deploy.sh`
  makes. Only `kgsm-assistant-service.service` is enabled — `kgsm-rag-indexer.service` stays opt-in.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

### Changed (v1.25.1) — webhook surfaces blueprint events for cache invalidation

- **The `POST /events` webhook now logs a typed line for `blueprint_*` events** alongside the
  generic envelope log. kgsm emits `blueprint_created`/`_updated`/`_removed` (Phase 2 of
  `blueprint-editor-plan.md`) when a game's `*.bp.yaml` is edited through the Control Panel or
  authored by the assistant — the webhook has always invalidated the inventory unconditionally on
  every event, so blueprint edits already dropped the assistant's blueprint catalog cache; the new
  log line just makes a web-originated edit's cache-bust visible to an operator skimming
  `journalctl` for "why did the assistant see the new values on the next turn". No behaviour change:
  the `IInventoryInvalidation.Invalidate()` call site is unchanged. The 8 manual
  `_invalidation.Invalidate()` calls inside `BlueprintAuthoringAggregator` stay as belt-and-braces —
  they are the CLI surface's only freshness path (the CLI has no webhook), and the plan frames them
  as best-effort redundancy on top of the event subscription.

### Changed (v1.25.0) — honest, evidence-driven assistant; `open_ports` resolves its own ports

- **`open_ports` no longer takes a `ports` argument — it reads the instance's configured (blueprint)
  ports deterministically from kgsm.** A real chat session surfaced the model guessing `ports:"default"`
  (the parser rejected it, the dispatcher returned an `Error:` tool result, and the model then
  fabricated "I've staged the request" with nothing staged). The tool now takes only the instance name
  (+ optional `include_router`); the handler fetches the structured port spec from kgsm's `instances
  info` via a new `IServerOperations.GetConfiguredPortsAsync`, renders it to the canonical UFW string
  through kgsm-lib's `PortMappingExtensions.ToUfwSpec`, and re-validates it through the single
  `PortSpecParser` so the stage-time and confirm-time paths never drift. An instance with no configured
  ports returns an honest error and stages nothing. There is no model-supplied port to guess wrong.
- **The system prompt now enforces an honesty + evidence contract.** The Preamble tells the model that
  a tool result beginning with `Error:` is a failure (never narrate it as staged/success — retry with
  corrected arguments or relay the error), that a status question must be backed by a fresh tool call
  this turn (especially right after a mutation/confirmation — never from memory), and that it reports
  measured values or "unknown", never invents. `ActionsAllowed` extends the "NEVER claim …" umbrella to
  "had its firewall ports staged" and adds "read the tool's result before you narrate it." `ActionsAuto`
  adds "re-verify with a fresh status read after a lifecycle verb runs." A light proactivity nudge has it
  offer to verify a mutation once the user confirms it. These target the two remaining failures from the
  same session: the fabricated staging claim (the model narrated past an error) and the assumed
  post-action status (the model answered from memory without a tool call).

### Changed (v1.24.0) — blueprint finalize is STREAMED (progress + heartbeats)
- **`POST /confirm` now STREAMS a blueprint finalize as Server-Sent Events** when the caller sends
  `Accept: text/event-stream` (the api relay does). A finalize is minutes of test-install → boot →
  verify → bounded-repair with long *silent* stretches (a SteamCMD download, a boot-log poll). Delivered
  as a single buffered response, that silence is a multi-minute idle socket that an idle-connection reaper
  on a remote path (NAT, a middlebox, the browser) drops — after which the chat's "verifying" card spun
  forever with no terminal result even though the finalize had completed server-side. Streaming fixes both
  halves: the pipeline's own `ITurnProgress` steps (research / install / verify / repair) are relayed as
  `progress` frames so the user sees it advancing; a keep-alive heartbeat every 15s keeps bytes flowing so
  no reaper fires; and a terminal `result` frame carries the whole `ConfirmResponse` (the same payload a
  buffered caller gets) so the card ALWAYS reaches a terminal state. A non-streaming caller (CLI, a plain
  JSON client) keeps the buffered `ConfirmResponse` contract unchanged. New `SseConfirmWriter`; new
  `TurnStream.Result` event name.

### Added
- **The assistant can now REVISE an open blueprint draft from chat (`revise_blueprint`).** Previously the
  only blueprint tool was `create_blueprint` (the initial draft); when a user asked to change or populate a
  draft ("populate the metadata"), the model had no way to do it and would falsely claim it had — a
  fabrication. A new `revise_blueprint` tool takes the complete updated YAML, re-validates it through the
  same structural + `$instance_*` placeholder funnel as finalize, and re-shows a fresh editable draft (no
  test-install). The turn now carries the draft's CURRENT content (the SPA sends what's in the editor, edits
  included) injected into the model's context, so it revises the actual content the user sees rather than a
  stale copy — tool results aren't replayed into later turns, so this is the only way the model can see the
  draft. `revise_blueprint` is offered ONLY on a turn that carries an open draft (authorized callers,
  authoring enabled), kept out of the default catalog. The prompt makes the anti-fabrication rule explicit:
  the model must never claim it changed the draft unless `revise_blueprint` actually succeeded.

### Changed
- **Assistant now drafts a missing-game blueprint in one turn instead of stalling.** Asked to add a
  game the catalog lacks, the model would run a manual `search`/`list_blueprints`, announce "I'll go
  research this and come back," and then STOP — the user had to say "continue" before `create_blueprint`
  ever ran. The authorized system-prompt stances and the `create_blueprint` tool description now state
  that the tool is self-contained (it does its OWN research and drafting) and must be called DIRECTLY in
  the same turn — no separate pre-search, no re-listing the already-injected catalog, no "I'll research
  and return" narration. Calling it IS how the work starts; after it returns, the model reports the draft
  is ready to review and save.

### Fixed
- **Blueprint-review Save was denied on a Discord-less relay host ("You don't have permission to add a
  blueprint to the catalog").** The `/confirm` handler re-derived action authority ONLY from the Discord
  bot, unlike `/turn` which honors the trusted relay's `X-Relay-Can-Act` header. On a host fronted by
  kgsm-api with no Discord OAuth configured, the propose (turn) was authorized but the finalize (confirm)
  was not — so the draft appeared but Save failed. `/confirm` now derives authority exactly as `/turn`
  does: the relay's `X-Relay-Can-Act` on the trusted-relay path (ANDed with `ActionsEnabled` + a
  configured confirmation key), Discord only for a direct session bearer. (Pairs with the kgsm-api fix
  that forwards `X-Relay-Can-Act` on the confirm relay — its `/assistant/confirm` is operator-gated, so
  the header is the verified tier.)
- **Blueprint research no longer non-deterministically fails to find the launch script on long docs.**
  Two faults in the agentic research sub-loop (`AgenticBlueprintResearch`) made `create_blueprint`
  succeed or fail on the *same* game+guide from run to run:
  - **The fetch budget was spent on identical re-reads.** A `fetch_url` of a URL the model had already
    read still counted against the six-page budget (and re-issued the request), so a model that spun on
    one long wiki page burned the whole budget without ever gathering a second source. A repeat request
    is now short-circuited — no request, no budget spent — with a note telling the model to fetch a
    *different* source or finish, freeing the budget to reach a compact page that actually names the
    launch script.
  - **Per-page truncation was a plain head cut**, which on a big MediaWiki article (huge nav + full
    table of contents + a requirements block before the "Starting the server" section) kept only
    boilerplate and dropped the launch instructions extraction needs. Long pages are now sliced to keep
    the head *plus a window centered on the first launch-relevant section* (SteamCMD app-update line,
    the `.sh`/binary launch, headless args, the ready log), so the launch script reaches synthesis.

  Research also now logs the gathered URLs and the final feasibility/field-count/source (synthesis vs
  regex fallback), so a failed draft is diagnosable from the journal.

### Added
- **CLI parity for the blueprint-review checkpoint** (`assistant-blueprint-review-plan.md` P4). When the
  assistant stages a `Blueprint` confirmation, the CLI now opens the drafted YAML in the user's editor
  (`$VISUAL` → `$EDITOR` → `nano`) for the mandatory review, then finalizes the saved text via
  `FinalizeBlueprintAsync` — the surface-agnostic parity of the in-chat Monaco card, including the
  repair-exhaustion **re-edit loop** (a `DraftReady` outcome re-opens the editor with the returned draft
  and the boot log that explains why it didn't come up). Saving an empty file, or declining the
  `[y/N]` test-install prompt, abandons cleanly (never a failure); a non-interactive stdin prints the
  proposal without running it, as with every other confirmation. This also gives the fastest end-to-end
  live-test loop for the feature without a browser.

- **In-chat review checkpoint for assistant-authored blueprints** (`assistant-blueprint-review-plan.md`).
  `create_blueprint` now DRAFTS only — it researches and builds the config, then returns an editable
  `DraftReady` card carrying the rendered YAML and stages a `Blueprint` confirmation; the test-install +
  verify runs later, only when a permitted human saves the (possibly edited) config. Finalize reuses the
  confirmation mechanism (new `ConfirmationKind.Blueprint` + `IServerAssistant.FinalizeBlueprintAsync`,
  returning the rich card rather than a text line); the Service `/confirm` accepts the edited YAML on
  `ConfirmRequest.EditedContent` and returns the outcome card plus, on a recovery `DraftReady`, a fresh
  Blueprint token for the re-edit loop. The edited YAML is untrusted — it re-enters the full safety funnel
  (structural parse via `IBlueprintFiles.TryParse`, the `$instance_*`-only placeholder guard, then the
  engine readback-validate and empirical boot). When the autonomous repair loop exhausts, the review path
  hands the last draft plus its boot evidence back as an editable `DraftReady` for another edit instead of a
  dead-end failure; the autonomous `create_blueprint`-equivalent (`AuthorAsync`) keeps its terminal
  `Failed`. Stateless — no authoring session is held between draft and finalize (everything is re-derived
  from the edited YAML). Consumes kgsm-lib 1.42.0 (`IBlueprintFiles.Render`/`TryParse`).

### Changed
- **Blueprint-authoring pipeline is split at the draft boundary (no behavior change).** The one
  `AuthorAsync` pipeline is carved into a draft half (gate → slug → existence guard → research →
  feasibility → build, touching no write authority) and a finalize half (persist → validate → test-install
  → verify → repair → keep/stash). `AuthorAsync` runs both back-to-back, so the autonomous outcome is
  identical; the split is the seam the in-chat human-review checkpoint (`assistant-blueprint-review-plan.md`)
  hangs off — the draft is returned for editing, and finalize runs on the edited draft across a
  confirmation. A `DraftReady` outcome is defined for that checkpoint (not yet emitted).
- **"Needs a Steam account" is now MEASURED, not inferred.** Blueprint authoring no longer guesses from
  page phrasing whether a game's server files require an owning Steam account — that inference over-declined
  games (e.g. Barotrauma) whose servers actually download anonymously, killing them at the feasibility gate
  before any install. Instead the pipeline attempts the anonymous test-install and checks the result: a
  Steam-account-owned title downloads NOTHING under anonymous login (SteamCMD connects, reports
  `Update state (0x0)`, and the install "succeeds" with an empty install dir), so an empty install dir after
  a successful install is the honest signal that the files aren't anonymously downloadable — surfaced as the
  "needs an owning account, add it manually" outcome. Anonymously-installable games now proceed to verify
  instead of being wrongly declined. The `requires_steam_account` inference is removed from both the LLM
  synthesis prompt and the deterministic extractor's phrase gate.
- **Blueprint-authoring readiness verification reads the full boot log, not a 3-line tail.** The verify
  step matched the startup-success regex against the status snapshot's `recent_logs` (only the last three
  lines), so a ready line that printed during boot and scrolled away was missed and a healthy server read
  as "never came up". It now pulls the full captured log via kgsm-lib's log reader and matches against all
  of it — for both the readiness check and the repair step's boot-log evidence. Two further readiness
  improvements: (1) when a draft carries no startup-success regex and the server binds no detectable local
  port (relay/NAT-punch servers), verification falls back to a curated set of well-known ready lines
  ("Server started", "Opened Steam server", …) matched only while the server is running, and writes the
  observed line into the kept blueprint so it ends with a real, measured readiness signal; and (2) a probe
  that comes up and then exits (a bad argument, a missing dependency) fails the attempt immediately instead
  of waiting out the whole timeout, so a repair cycle starts in seconds. The verify timeout is raised to
  240s (a GB-scale server can take minutes to cold-boot; the crash-exit keeps that ceiling from being paid
  on a server that already died).

### Added
- **Blueprint authoring repairs a failed draft from the real install, not just from the web.** When a
  drafted config test-installs but doesn't boot + listen, the pipeline now reads two ground-truth
  evidence sources the web research pass never had — the actual installed directory tree (what the
  download really put on disk, so the executable's true name and location are read rather than guessed)
  and the game's own launch scripts + boot log — and an LLM repair step proposes corrected launch fields
  for the next attempt. This replaces the old blind retry (which re-ran the identical draft) with an
  observe→correct loop: a wrong executable is swapped for the real one on disk, a rejected argument is
  fixed or dropped from the server's own error, and a readiness line found only in a game-written log
  file becomes the `startup_success_regex` (with output redirects like Unity's `-logfile` dropped so the
  readiness line reaches the monitored stdout). The anti-fabrication discipline is stronger than
  synthesis because the evidence is ground truth — a proposed executable is rejected unless it actually
  appears in the install tree — and the empirical boot + listen check stays the final backstop. The loop
  stops as soon as repair has no evidence-based change to make, so it never flaps on an unfixable source.
  New `IBlueprintRepair` port (fail-closed `DisabledBlueprintRepair` default, `LlmBlueprintRepair` when a
  model is wired); the persist→install→verify→**repair** loop runs up to three attempts.

### Fixed
- Blueprint authoring draft-quality hardening, from a 7-game validation batch (all draft/research
  quality — the empirical boot-verify backstop already prevented any bad blueprint from being kept):
  - **Fabricated launch-arg variables are rejected.** Synthesis sometimes invented `$SERVER_PORT` /
    `$SERVER_NAME` / `$SERVER_PASSWORD` — KGSM defines no such variables, so they resolved to empty at
    runtime and the server never booted. The prompt now forbids any `$…` token outside the three real
    `$instance_*` placeholders, and a deterministic guard drops the whole `executable_arguments` string
    if it still references a foreign variable (the server boots on its defaults instead of broken flags).
  - **Interpreter-launched servers are modelled.** A Java server's executable is `java` with
    `-jar <file>` in the arguments (not the `.jar`); a .NET server's is `dotnet` with `<file>.dll`.
  - **`executable_subdirectory` and `client_steam_app_id` are extracted** — a binary that runs from a
    subfolder (e.g. `bin/x64/factorio`) is split into subdirectory + filename instead of crammed into
    the executable field, and the client/store app id is captured alongside the dedicated-server id.
  - **Arguments stay minimal and portable** — the prompt drops cosmetic flags and absolute host paths
    (e.g. `/opt/<game>/server-settings.json`) that won't exist on a fresh install.
  - **`requires_steam_account` no longer false-positives.** A different dedicated-server app id from the
    client id means a free standalone server app exists → ownership is NOT required; generic "you must
    own the game to play" prose about the client no longer declines an anonymously-installable server
    (both the synthesis rule and the deterministic-fallback phrase gate).
  - **The game's own wrapper launch script is preferred over the raw binary.** When a game ships both a
    wrapper (e.g. `start_server.sh`, `_launch.sh`) and a raw binary (e.g. `valheim_server.x86_64`), the
    script is the executable — it sets up the runtime (LD_LIBRARY_PATH, working dir) the binary needs.
    (This is the same "look past the wrapper" instinct as the Docker-entrypoint exclusion, but inverted:
    a Docker `entry.sh` is still excluded; the game's own native launcher is preferred.)
  - **The agentic researcher hunts the server executable filename as a first-class goal** (a run that
    found the app id and port but not the launch file previously produced no draft).
  - **Slugs match the catalog convention** (`abioticfactor`, `corekeeper` — concatenated, no hyphens),
    so the existence-guard recognises an already-present multi-word game instead of drafting a duplicate.

### Added
- Blueprint authoring detects when a game's server files require a Steam account that OWNS the game
  (e.g. Starbound) and stops with an honest, specific reason instead of failing opaquely at the
  anonymous test-install. Research/synthesis infers the requirement from signals (the docs say you must
  own the game, a `+login <username>` steamcmd command, or the server downloading under the paid game's
  own app id) — this is a warning that gates the install, not a written blueprint value, so reasonable
  inference is allowed here (a false positive only declines a game the user can still add manually). New
  `BlueprintFeasibility.RequiresSteamAccount`; the user-facing outcome says the game needs an owning
  account and can be added manually. A deterministic phrase gate in the regex extractor catches it on the
  fallback path too.

### Changed
- Blueprint field synthesis maps a documented world/save NAME or data/install PATH onto KGSM's
  `$instance_*` runtime placeholders in `executable_arguments` (`$instance_level_name`,
  `$instance_saves_dir`, `$instance_install_dir`), which KGSM substitutes at server-creation time —
  instead of keeping a doc placeholder like `[Save name]` verbatim. A launch arg from a page that read
  `-world [Save name]` now drafts as `-world $instance_level_name`, matching how the shipped blueprints
  are written. Only a name/path the docs clearly leave for the user is substituted; every other flag
  stays exactly as the page shows it.

### Added
- Blueprint research is agentic: a bounded research sub-loop drives its OWN `search` + `fetch_url` calls
  to gather the authoritative pages for a game's native Linux server, then hands them to the synthesizer
  for sourced extraction. The model does source SELECTION (run several targeted searches, follow the
  official/wiki pages, look past community Docker wrappers) — what it is good at — while synthesis does
  the guarded extraction over what was fetched. Replaces the fixed one-query/top-3 pass, which
  mis-selected sources (it landed on a Docker wrapper and never fetched the page documenting the native
  launch). Bounded (a handful of model rounds and fetches — research is a background step, not a crawl)
  and degrades gracefully: any loop failure, or a run that gathers nothing, falls back to the fixed pass,
  and synthesis still falls back to the deterministic extractor. New `AgenticBlueprintResearch`
  (the registered `IBlueprintResearch`); `BlueprintResearchAggregator` becomes its fixed-pass fallback.

### Added
- Blueprint research extracts native-server fields by model synthesis first, falling back to the
  deterministic regex extractor. One LLM call reads the fetched pages in context and returns sourced
  fields as JSON — it can pick the OFFICIAL native launch method over a community Docker wrapper, which a
  regex cannot. No-fabrication is layered: the model is told null-not-guess and to cite a source URL per
  field; a field is kept only when it cites one of the pages actually fetched; copy-from-source fields
  (`executable_file`, `steam_app_id`) must also appear verbatim in the fetched text; and the pipeline's
  empirical boot+listen verification is the final backstop. Any failure (no model wired, transport error,
  unparseable reply, or no required field sourced) returns null and research falls back to the extractor.
  New `IBlueprintSynthesizer` port (+ fail-closed `DisabledBlueprintSynthesizer`) and `LlmBlueprintSynthesizer`.

### Changed
- The research fact extractor sources `executable_file` from a launch script/binary that is either
  invoked explicitly (`./X.sh`, `bash X.sh`, `sh X.sh`) OR named like a launcher
  (`StartServer-nogui.sh`, `*server*`/`*start*`/`*launch*`/`*run*` + `.sh`/`.x86_64`/`.x64`) — many
  dedicated-server docs name the script without a `./` prefix. The launcher-name gate keeps an
  unrelated `steamcmd.sh`/`install.sh` in the same page from being mistaken for it; a wrong pick is
  still caught by the empirical boot+listen verification.

- The research fact extractor sources `steam_app_id` only from an unambiguous dedicated-server download
  context — a steamcmd `+app_update <id>` command, or text that explicitly names the dedicated/server
  app id. A bare store-page `app/<id>` URL or a plain "App ID: <id>" is no longer matched: that is
  typically the CLIENT app id (installing against which is wrong), so the field stays unsourced (the
  schema's `0` = "not a Steam download") rather than carrying a misattributed client id.
- Blueprint research reaches the web provider (`IWebSearch`) directly instead of the local-index-first
  `ISearch` aggregator. Authoring a blueprint needs fetchable third-party pages (an official server doc,
  a setup guide); a local-first search answered the "how do I host X" query from this host's own
  documentation index and never reached the web, so research found no page to fetch whenever the RAG
  index was enabled.
- The research fact extractor now sources `executable_arguments` (a documented headless launch command —
  the flags a server needs to boot non-interactively instead of hanging at an interactive prompt and
  never listening) and `startup_success_regex` (a real server-ready log line, taken only when it appears
  verbatim in a fetched page — a secondary readiness signal alongside the primary port-reachability
  check). Both stay unset when a page doesn't document them — never fabricated.
- Drafted blueprint ports render in UFW format (`<port>/tcp|<port>/udp`), the shape kgsm's schema
  expects, rather than a docker-style `host:container` mapping.

### Added
- `create_blueprint` narrates its own progress over the SSE turn stream while it is still running —
  a new `progress` frame (`{ tool, key, label, status }`, `id` omitted — the ambient sink that reports
  it has no access to the tool-call id the generic agent loop mints) fires as each pipeline stage
  begins: `research` → `feasibility` → `draft` → `install` → `verify` → `teardown`, always landing
  before the tool's own terminal `tool.result`. A feasibility-fail (or earlier) honest stop reports
  only the steps it actually reached — never a fabricated later stage. Carried by a new per-turn
  ambient sink, `ITurnProgress` (mirrors `IConfirmationContext`'s scope shape), opened only on the
  streaming `RunStreamAsync` path; the buffered `RunAsync` path never opens it, so `create_blueprint`
  still returns exactly one card there, unchanged. `BlueprintAuthoringData` gains a `Reason` field (the
  same honest "why not" text carried on the envelope's `Summary`, now readable without parsing prose)
  and the tool's `ResultRef` subject now carries the canonical blueprint **slug** once one is known
  (not the raw game name) — the id a web install handoff sends straight to `POST /servers`.
- A new `create_blueprint` tool AUTHORS a game type genuinely missing from the catalog: it researches
  the game online (via `search`/`fetch_url`), drafts a native-Linux blueprint from sourced fields only
  (an unsourced field stays `null`/default, never fabricated), test-installs it under a reserved
  `__bp_probe_<name>__` instance name, verifies empirically that it boots and listens (polling
  `IServerOperations.GetHealthSnapshotAsync` against a bounded timeout, matching the sourced
  `startup_success_regex` when one was found), and keeps the blueprint only if that succeeds —
  otherwise the catalog stays clean and the attempt is stashed for admin review. Runs autonomously in
  one step (authorized, no propose→confirm — the new `LlmTools.AuthorizedActions` tier), because it
  touches nothing of the user's; the test-install probe is guaranteed torn down in a `finally` on every
  exit path, and the Service host runs a one-shot startup sweep (`BlueprintProbeSweepService`, the
  repo's first `IHostedService`) that removes any probe a prior crash left behind. Gated end-to-end by
  a new `BlueprintAuthoring` config section (`Enabled` false everywhere by default — the pipeline never
  touches kgsm-lib's write-side authorities until an operator opts in) and a computed
  `BlueprintAuthoringFlags.Available` mirroring `FetchOptions`'s pattern. New `IBlueprintAuthoring` port
  (the model-facing seam; the real `BlueprintAuthoringAggregator` lives in Infrastructure since it needs
  kgsm-lib's `IBlueprintFiles`/`IBlueprintService`/`IInstanceService` directly), a new `IBlueprintResearch`
  port + `BlueprintResearchAggregator` (deterministic composition over `ISearch`/`IWebFetch`, no nested
  model call), and a new `IBlueprintAttemptStore` port + filesystem-backed admin stash. Consumes
  kgsm-lib 1.40.1's new `IBlueprintFiles` write authority. Eval corpus bumped to v7 with a `create_blueprint`
  routing group (B), including a disambiguation pair against `install_server` for a blueprint that
  already exists.

## [1.10.0]

### Added
- A new `fetch_url` tool lets the assistant read the full text of ONE specific web page by URL — an
  official docs page, a Steam store page, a raw Dockerfile, and so on — distinct from `search`, which
  only returns provider-summarized hits and cannot fetch a page. Backed by a new `IWebFetch` port
  (mirroring `IWebSearch`'s shape: a fail-closed `DisabledWebFetch` default, offered only when a real
  adapter is configured) and a config-gated, budget-capped `HttpWebFetch` adapter (`WebFetch` config
  section, no API key needed — its own `Enabled` flag is the sole gate). The adapter enforces a scheme
  allowlist (http/https only), an SSRF guard that rejects loopback/private/link-local/multicast/reserved
  addresses (including the `169.254.169.254` cloud-metadata address) and re-validates on EVERY redirect
  hop (auto-redirect is disabled; the adapter follows manually), a size cap (truncates rather than
  buffering unbounded), a timeout, and content-type filtering (HTML is stripped to readable text with a
  lightweight dependency-free extractor; `text/plain` and similar raw files pass through verbatim;
  binary content-types are refused honestly). `fetch_url` is offered in the read-only tier alongside
  `search`, gated by `FetchOptions.Available` (computed the same way as `SearchOptions.Available`), and
  capped per message in the assistant gate independently of the search cap.

## [1.9.0] - 2026-07-19

### Added
- A new propose-only `write_file` tool lets the assistant overwrite a game server's OWN config file
  (e.g. Palworld's `PalWorldSettings.ini`), distinct from `set_config_value` (KGSM's own `.config.ini`).
  The model reads the current file (and any default/reference file) in full, composes the COMPLETE new
  content, and stages it; a human confirms against a preview before anything is written. Reuses the
  read path's instance-directory jail verbatim (`ResolveInstanceBoundaryAsync` / `IsWithin` / symlink
  re-check / `IsNonRegularFile`) via a new `IServerOperations.WriteInstanceFileAsync` — a new file may
  only be created inside an already-existing in-jail directory, never a deep tree. The write is capped
  at 10 MB, atomic (temp file + rename in the same directory), and backs up a non-empty existing target
  to a sibling `.kgsmbak` (overwritten each time — last-good, not a history) before replacing it. Always
  stages, even on an auto-accept turn — a whole-file overwrite always gets a human look at the diff.
  On the Service (HTTP/SSE), the confirmation token carries an opaque SQLite-backed pending-write id
  instead of the file body (a 10 MB body can't ride a stateless HMAC token); the real content is
  rehydrated single-use at confirm time. The CLI carries the real content in-process, unaffected by the
  cap. `ConfirmationKind.WriteFile` is `Destructive`-exempt (the `.kgsmbak` + preview are the friction).

### Changed
- System-prompt guidance for the config-edit flow: to change a game's own config, read a known path
  directly rather than walking every directory level with `list_files`, and PROPOSE a requested change
  by CALLING `write_file` (the staging IS the confirmation prompt) instead of asking in prose first; an
  empty or missing game config file is normal, not an error. This matches the live behaviour of
  `gemma4:12b` on a deep config path.
- The agent iteration cap is raised from 8 to 16 on both assistant surfaces (CLI + Service
  `LlmAgent:MaxIterations`), so a multi-step edit flow (navigate → read the file → read the reference →
  propose) has headroom to reach the staging step. Unchanged for turns that finish early; the cap is a
  maximum, not a target.

## [1.8.1] - 2026-07-19

### Changed
- System-prompt routing guidance splits the port/network question across the two tools it now spans:
  "is the server running / what port does it listen on" points at `get_status`, while "is its port open
  / is it reachable from outside" (firewall or router reachability) points at `get_network`. Previously a
  single line sent all "network details" to `get_status`, written before `get_network` existed. Routing
  is measured green either way (`gemma4:12b` already picked the right tool); this makes the instruction
  match the catalog.

## [1.8.0] - 2026-07-19

### Changed
- `get_network` now reports BOTH network layers for a server: the host firewall (as before) AND its
  **router / UPnP port forwards**, read from the kgsm-watchdog via kgsm-lib's `IWatchdogClient` through a
  new neutral `IUpnpInfo` port + `KgsmUpnpInfo` adapter (a separate authority from the firewall, never
  conflated). The two axes fail independently and honestly: a queried router that owns no forwards is a
  real "no forwards", distinct from a router that couldn't be reached (`RouterUnavailable`) or a watchdog
  that couldn't be reached (`DaemonUnavailable`) — neither is ever read as "nothing forwarded". The card
  gains a `UpnpState` + `Forwards[]` block and is attached whenever EITHER axis has real measured
  structure. Fails closed: with no watchdog wired the router axis reads as unavailable and the assistant
  still boots standalone.
- `open_ports` gains an optional `include_router` flag — when set, the confirmed command ALSO opens the
  router / UPnP forward for the same ports (via `IWatchdogClient.OpenUpnpAsync`), so a server can be made
  reachable from the internet in one step. The router leg honors the instance's `enable_port_forwarding`
  gate at the watchdog (a gated-off server is honestly `skipped`, never a fabricated forward) and reports
  its outcome (applied / skipped / failed / watchdog-unavailable) as a separate clause alongside the
  firewall outcome. Default (flag off) is host firewall only. The opt-in rides the existing
  confirmation token (on `ConfigKey`), so there is no new token field and no new `ConfirmationKind`.

## [1.7.0] - 2026-07-19

### Added
- A `get_network(instance_name)` read-only tool — reports one instance's HOST-FIREWALL picture: the
  ports KGSM has opened for it (via the kgsm-firewall authority), the active backend, and whether it's
  enforcing. Backed by the neutral `INetworkInfo` port and the pure `Network.NetworkReport` composer
  (no nested model call); the `KgsmNetworkInfo` adapter reaches the firewall authority through
  kgsm-lib's `IFirewallService` (`ListOwnedAsync` + `BackendAsync`) and fails closed — an unreachable
  authority reads as an honest "firewall unavailable", never a fabricated "nothing open". Covers the
  host firewall ONLY: router/UPnP port forwarding is not observable from the host, so it is never
  reported or implied. Offered to everyone (reveals no file contents); only an `Available` read carries
  a `NetworkData` card, an unavailable read stays summary-only.
- An `open_ports(instance_name, ports)` propose-only staged command — opens HOST-FIREWALL ports for an
  instance on human confirmation. Staged like every command (never runs in a turn); on confirm it calls
  kgsm-lib `IFirewallService.EnsureOpenAsync` through `INetworkInfo`, mapping the authority's precise
  outcome (applied / applied-but-not-enforcing / no-op / unsupported / unreachable) to an honest result,
  never a fabricated open. The `ports` argument accepts `port`, `port/proto`, `start:end`, and
  `start:end/proto` forms (comma/pipe separated; a missing protocol opens both tcp and udp), parsed once
  by `Network.PortSpecParser` and carried on the confirmation token as a canonical string. Opens the
  host firewall only — it does NOT configure router/UPnP port forwarding.
- `run_health_check` gains a fifth **port-reachability** check — for a running instance, whether its
  configured ports are currently bound (host-local `ss` probe via kgsm-lib `watcher ports test`).
  Reachable → pass; running-but-unbound → warn (it may still be starting), never a hard fail; a stopped
  server, an instance with no ports configured, or a failed probe → skip, never a fabricated pass. This
  is host-local port binding, distinct from `get_network`'s firewall-rule view.

## [1.6.0] - 2026-07-18

### Added
- A `trace_root_cause(instance_name, range?)` tool — the toolbox's capstone aggregator (plan §3.4/
  §7·Q1): a DETERMINISTIC composition of one instance's engine event timeline, a metrics window, and
  its health/status snapshot, run through a fixed rules table of known KGSM failure signatures. No
  nested model call anywhere in the path — the pure `RootCause.RootCauseAggregator` fetches nothing
  itself and reasons about nothing; the dispatcher fans the three reads out in parallel
  (`IEventHistory` + `IServerMetrics` + `IServerOperations.GetHealthSnapshotAsync`, the same neutral
  inputs `get_audit_log`/`get_performance`/`run_health_check` already use) and the aggregator only
  pattern-matches over what came back. `instance_name` is REQUIRED (unlike the audit tools — root
  cause is always about one server); `range` defaults to `24h`. The rules table: a start/restart that
  crashed or failed within 3 minutes without reaching "ready" reads as a port-conflict/bind-failure
  shape (`Likely` — kgsm's event log records THAT a start didn't take, not WHY); one or more crashes
  within 15 minutes of an update finishing reads as update-triggered (`Likely` for one, `Confirmed`
  "crash loop" for two or more); the health snapshot's own critical-disk verdict coinciding with a
  deploy/download/uninstall failure reads as disk-full (`Confirmed` — reuses `HealthCheckAggregator`'s
  disk judgment rather than a second threshold); the most recent run-state event saying
  started/restarted/ready while the live snapshot reads not-running is a split-brain (`Confirmed` — a
  direct, measured contradiction, not an inference). When nothing matches, the result is an honest
  ranked correlation of the most salient recent events at `Possible` — never a guessed cause. Every
  source degrades independently: an unreachable event timeline means no rule can be evaluated at all
  (honestly worded, not a fabricated "nothing happened"); a failed health snapshot only disables the
  two rules that need it (disk-full, split-brain); an unreachable metrics window only empties the
  supporting CPU/memory context facts. Emits the shared `ToolResult<RootCauseData>` envelope with a
  ranked `Findings` list (each carrying its own evidence — the specific events, metric facts, and
  health checks that produced it) and a deterministic summary authored from the top finding — the
  model narrates it, it never authors it.

### Changed
- `Metrics.PerformanceReport`'s `Stats`/`FormatBytes` helpers are now `internal` (were `private`) so
  `RootCauseAggregator` reuses the exact same avg/peak computation and byte formatting for its
  metrics-window evidence instead of a second copy.

## [1.5.0] - 2026-07-18

### Added
- Two engine-event-history tools, `get_audit_log` (`instance_name?`, `window?`) and
  `get_change_timeline` (`instance_name?`, `range?`), read directly from the kgsm-monitor's
  `GET /events` over its unix socket — the assistant reads the monitor's engine-event store the same
  way `get_performance` reads its metrics, never through kgsm-api (leaf independence). Both accept an
  OPTIONAL instance (omit for every server on the host) and a window token (`1h`/`24h`/`7d`/`30d`, an
  unrecognized/omitted token honestly falls back to the tool's default — `24h` for the audit log,
  `7d` for the timeline — never an error). `get_audit_log` is the unfiltered "what happened" feed,
  most-recent-first; `get_change_timeline` shares the same source narrowed to durable state changes
  (install/uninstall/update/version-update/backup/port-open/port-close) and excludes routine
  start/stop and player join/leave. Both emit the shared `ToolResult<AuditData>` envelope with a
  deterministic, type-counted grounding summary (e.g. "6 events for factorio-test in the last 24h: 2
  starts, 1 crash, 1 update…") authored by a pure `AuditReport` composer — never the model. An event
  with no recorded actor renders as unknown, never defaulted to a placeholder like "system"; an empty
  window is an honest "no events/changes recorded"; an unreachable monitor is an honest "couldn't
  read", explicitly not a claim that nothing happened. The capability is additive and fails closed —
  a fail-closed `IEventHistory` default reports the monitor unavailable when none is wired, so the
  assistant composes and boots standalone (reuses the existing `Monitor:SocketPath` config key, no
  new configuration).

## [1.4.0] - 2026-07-18

### Added
- A `get_performance` tool that surfaces one running server's LIVE resource usage as a snapshot —
  CPU (as a percentage of one core), memory, network and disk-I/O throughput, on-disk footprint, and
  process count — read from the kgsm-monitor's latest `GET /metrics` frame over its unix socket. It
  emits a structured `PerformanceData` card alongside the model's grounding text (the shared
  `ToolResult<PerformanceData>` envelope): a `live` read carries the measured values and a card; a
  not-running server or an unreachable monitor stays summary-only, worded honestly ("couldn't read",
  never "idle"). An unmeasured axis (`null`) is omitted from the summary rather than shown as 0. The
  capability is additive and fails closed — a fail-closed default reports the monitor unavailable when
  none is wired, so the assistant composes and boots standalone. New `Monitor:SocketPath` config key
  (default `/run/kgsm-monitor/metrics.sock`).
- `get_performance` also answers TREND questions: an optional `range` argument (`1h`/`24h`/`7d`/`30d`)
  switches from the live snapshot to a windowed history read, pulled on demand from the monitor's
  `GET /metrics/history` (the monitor is the single source of truth for metrics history). The card then
  carries the per-metric time series (a chart the surface renders) and the grounding summary states the
  CPU + memory avg/peak over the window (the numbers the model can't read off a chart). An empty window
  is an honest "no history recorded yet", and an unreachable monitor an honest "couldn't read" — neither
  is narrated as idleness.

## [1.3.0] - 2026-07-17

### Added
- The `search` tool now emits a structured `SearchData` card alongside its grounding text, so a
  surface (the web chat) can render the cited passages instead of a bare "searching" pill. The
  aggregator returns the shared `ToolResult<SearchData>` envelope: the model still reads the exact
  same `Summary`; the card carries each passage's provenance (local docs vs web), source
  (doc path / URL), title, snippet, and score, plus which rung answered (`LocalStrong`/`LocalWeak`/
  `Web`). An empty or "couldn't search" result carries no passages and stays summary-only — a card
  always has something to cite (never-fabricate).

## [1.2.0] - 2026-07-17

### Added
- `command.proposed` SSE frames now carry an optional `instanceName` for the `install` verb — the
  custom name a user asked for. `subject.id` is the blueprint for an install, so the name rides its
  own field; a surface that installs via an API endpoint passes it through so a named install lands
  the requested name instead of silently dropping it.

## [1.1.0] - 2026-06-30

### Added
- Initial versioned release.
