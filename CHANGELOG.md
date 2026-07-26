# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
