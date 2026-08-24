# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-llm` is the **LLM / assistant** leaf of the KGSM ecosystem: a local, tool-calling AI
assistant (runs on a model served from this host — no cloud LLM) that answers questions about, and
with authorization acts on, the game servers a `kgsm` engine manages. The workspace keystone is
`../system-architecture.md`; this repo's own orientation doc is **`docs/ARCHITECTURE.md`** — read it
first for the mental model.

## Commands

Everything is one .NET 10 solution, `TheKrystalShip.Llm.slnx`, driven from the repo root.

```bash
dotnet build TheKrystalShip.Llm.slnx -c Release
dotnet test  TheKrystalShip.Llm.slnx                       # ~500 tests, hermetic
dotnet test --filter "FullyQualifiedName~SomeTestName"     # one test / class
dotnet test TheKrystalShip.Kgsm.Assistant.Eval.Tests/*.csproj   # one suite by project
```

- **Live-model smoke tests are inert by default** — they no-op unless `KGSM_LIVE_OLLAMA=1` (and
  some need a kgsm host / pulled models). The default `dotnet test` is fully hermetic:
  `KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~CliLiveSmokeTests`.
- **`TheKrystalShip.Rag*` must publish Native-AOT-clean** — expect **0 ILC warnings**:
  `dotnet publish TheKrystalShip.Rag.Indexer -c Release -r linux-x64`.

Running the deployables (each is a thin host over the same backend; needs a reachable model server, and
the assistant surfaces also need `KGSM__Path` → a real `kgsm.sh`):

```bash
# CLI — one-shot / pipe / REPL
KGSM__Path=/usr/local/bin/kgsm dotnet run -c Release --project TheKrystalShip.Kgsm.Assistant.Cli -- "what's installed?"
# Service — HTTP/SSE; /health needs no secrets
dotnet run --project TheKrystalShip.Kgsm.Assistant.Service   # then curl http://127.0.0.1:5180/health
# Eval — reproducible benchmark (live run needs a model server; routing mode also needs a kgsm host)
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --shipped-prompts --transcript
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --seed 42   # ground-truth RAG lift chart
```

> `-c Release` on `dotnet run` reuses the Release build so stdout is just the answer; a bare
> `dotnet run` rebuilds in Debug and prints build output first.

## Deploying

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

Note the install prefix keeps the *assistant's* name, not the repo's: everything lands under
`/opt/kgsm-assistant`, configured from `/etc/kgsm-assistant/service.env`.

`setup.sh` is the only part that needs privilege. It chowns `/opt/kgsm-assistant` to you, seeds the
env file, creates the `service`/`cli`/`indexer`/`docs` subtrees and
the `/usr/local/bin/kgsm-assistant-cli` symlink, puts the real units in **user-owned**
`/etc/kgsm-assistant/systemd/` with the `/etc/systemd/system/` entries symlinked to them, installs a
polkit rule scoped to this project's units, and then verifies the grant by making the same
unprivileged `systemctl` calls the deploy will. Both units are installed but only
`kgsm-assistant-service.service` is **enabled** — `kgsm-rag-indexer.service` is opt-in, so
provisioning a host never silently starts indexing.

`/var/lib/kgsm-assistant` — the conversation database and the RAG index — is not in that list: both
units declare `StateDirectory=kgsm-assistant`, so systemd creates it owned by `User=` before
`ExecStart` and exports `$STATE_DIRECTORY`, which the Service resolves the database from and the
indexer's `--index` argument is written in terms of. Provisioning it therefore costs no privilege at
all, and the directory follows the user `deploy.sh` templates the units with.

That setup is what makes `deploy.sh` **need no privilege at all**: the prefix is yours so installing
is a plain file write, a changed unit is a plain file write into the user-owned directory, and every
`systemctl` verb goes through the polkit grant. It refuses **before building**, with *"run
`deploy/setup.sh`"*, on an unprovisioned host. If some *other* operation seems to need root, stop and
ask — don't reintroduce `sudo` into `deploy.sh`.

`deploy-common.sh` holds the paths/units/helpers both scripts share. The three files are
self-contained, so a standalone clone deploys with no other repo checked out; every `kgsm-*` repo
carries this same pattern. Cold-start runbook: `docs/DEPLOYMENT.md`.

## Architecture (the parts that span files)

The layer cake — **one brain, many surfaces** (full diagram + rationale in `docs/ARCHITECTURE.md`):

- **`TheKrystalShip.Llm`** — generic tool-calling **agent loop**. Knows nothing about KGSM;
  publishable standalone (a sibling Discord bot consumes it as a package). Owns the model
  round-trip, iteration cap, tool-output truncation, and conversation memory.
- **`TheKrystalShip.Kgsm.Assistant`** — the **brain**: tool catalog, system prompt, action policy,
  the `search` aggregator, and **ports** (`IRetrieval`, `IWebSearch`, kgsm command/query interfaces).
- **`TheKrystalShip.Kgsm.Assistant.Infrastructure`** — **adapters** binding the ports to reality
  (kgsm-lib, Tavily, the RAG index).
- **`TheKrystalShip.Kgsm.Assistant.Service` / `.Cli`** — the two surfaces; both compose the *same
  three DI calls* `AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` and differ only in I/O and
  *who the user is* (auth/authority).
- **`TheKrystalShip.Rag`** (read core) + **`TheKrystalShip.Rag.Indexer`** (write daemon) — a
  self-contained RAG subsystem; **`.Eval`** is the benchmark.

Things that bite if you don't know them:

- **Policy is inverted out of the loop.** The library never learns what "mutating" or "authorized"
  means. Every turn the *host* hands it an `AgentTurn` { prompt, fresh system prompt, the tool
  whitelist, a per-call `Gate` closure }. The offered tool set *is* the whitelist; the `Gate`
  authorizes each call and can hold state (e.g. an actions-per-message cap).
- **The prompts and the tool catalog are FILES, not code.** `deploy/prompts/` in this repo is the
  source; `deploy.sh` installs it to `<prefix>/prompts` with `rsync --delete` and the service reads it
  from there. Nothing equivalent is compiled in: a host without those files **refuses to start**,
  naming the file. The `.md` segments are re-read **every turn** (edit one, ask again — no restart);
  `tools.json` is read **once at startup**, because it is the contract between the model and the
  dispatcher and must not change under a turn in flight.
  ⚠ **A deploy overwrites that directory.** Tuning on a live host is the intended loop, but paste the
  wording back into `deploy/prompts/` or the next deploy discards it — the deploy is the commit.
  ⚠ **Tier membership stays in code** (`LlmTools.*Tier`). `tools.json` carries descriptions, parameter
  prose, types, `required` and `enum`; it does NOT carry which tier a tool is in, because that decides
  who is offered it and whether it is staged. A file that could move a staged command into the
  read-only tier would be a privilege escalation. `DiskToolCatalog` refuses a catalog that disagrees
  with the dispatcher in either direction — a tool the code can run and the file omits, or a tool the
  file invents and nothing implements.
- **Inject the live instance/blueprint list into the system prompt every turn.** This is
  load-bearing, not cosmetic — without it the model wastes a round-trip calling `list_instances` to
  "ground" itself before acting (a measured finding; see `docs/ARCHITECTURE.md` §"The agent turn"). The prompt is rebuilt
  fresh each turn for this reason.
- **A server has two names, and the list carries both.** Its **id** is generated by kgsm at install,
  never changes, and is what every tool argument, path and event is written in; its **display name**
  is free text somebody chose and changes whenever they like. The injected line is `- <id> — called
  "<label>" (game: X)`, followed by the sentence telling the model which of the two a tool takes,
  because a list of ids alone leaves "restart My Factorio" with nothing to match and a list of labels
  alone leaves it passing a string that resolves to nothing. `ResolveInstanceAsync` matches either and
  **always returns the id**; two servers sharing a label is a question for the user, since labels are
  decoration and are not unique. `install_instance`'s `instance_name` is the display name — the engine
  mints the id.
  ⚠ **An explicitly-chosen id is validated** (`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`), which is why a
  blueprint's test-install probe is `bpprobe__<slug>__`: the probe's name is an id this code minted
  and then addresses the log read, the file walk and the teardown by, and one the engine refuses is a
  blueprint that can never be verified.
- **A reply's shape is a per-turn input, and presentation only.** `POST /turn` takes an optional
  `style`; `"voice"` appends the spoken-delivery segment (`voice.md`, or `Llm:Voice`)
  after the injected lists, so it is the last thing the
  model reads. It rides the **turn**, not the leaf, because it describes where the answer lands and
  one leaf carries both surfaces — kgsm-bot sends it from a voice channel and omits it from a text
  channel under one identity. It reaches the system prompt and nothing else: the tools offered, the
  authority and the propose-then-confirm rule are identical in every style, and an unrecognised
  value fails open to the written answer. Measure a change to it with `kgsm-assistant-eval voice
  --shipped-prompts`, which reports reply length against a trajectory floor.
- **Memory belongs to an OWNER, not a conversation.** `remember`/`forget`/`recall` write things that
  outlast the conversation they were said in; a one-line index is injected into every later turn's
  system prompt (below the hashed template, like the live server lists — hashed in, every person
  would produce a different prompt id). The owner is the conversation id up to its second `:`
  (`MemoryScope`), so `web:{user}:{chat}` → `web:{user}`, and a room owns its own. It is **ambient for
  the turn** (`MemoryOwner`), never a tool argument, and the scope is opened in
  `ServerAssistant.ProduceStreamAsync` beside the confirmation/progress/search scopes — the yield-free
  flow the dispatcher runs on. Storage is append-only, latest-wins per key, in the conversation
  database. ⚠ **A memory carries what it was TOLD, never what a tool MEASURED** — a remembered port
  repeated months later is a confident wrong answer. The rule is prompt-enforced and **holds only when
  the model is thinking**: with `Llm:Think` on it writes no reading down, and with it off — which is
  what the Service ships — it writes one every time, against four measured wordings (`Eval/CLAUDE.md`,
  case `J2`). What bounds the harm is the recall-side framing, measured to keep the model calling the
  tool anyway even with a wrong memory in front of it.
- **Propose, then confirm — mutations never execute inside a turn.** A mutating tool call *stages*
  the action and returns a confirmation token; the user confirms out-of-band (`/confirm` on the
  Service, interactive y/N on the CLI). A run with no confirmation touches no server.
- **A staged action lives server-side; a client only ever holds a handle to it.** The Service keeps
  the resolved operation in `pending_confirmations` (the conversation store's SQLite file) and hands
  out a 32-hex-character handle — *what* would be done never leaves the process. The handle is
  single-use, expires with `Assistant:Confirmation:TtlSeconds`, and is redeemable only by the user it
  was staged for; the store enforces that itself, and leaves anyone else's handle standing rather
  than consuming it. One model for every surface: a browser and a Discord button carry the same
  thing, and no surface works around another's identifier limits.
- **A turn is a session, not a request.** `TurnSession` runs it with its own lifetime and broadcasts to
  every surface attached to that conversation; `POST /turn`'s SSE response is simply its first consumer.
  So the caller going away does not end it — a turn ends when its *person* has been absent for the grace
  window, or when any of their surfaces calls `DELETE /turns/{id}`. Frames are produced **once**, in
  the session, which is what makes two surfaces watching one turn agree by construction; never
  re-derive them per consumer. A conversation runs one turn at a time (the rest queue), and a queued
  turn re-derives its authority when it runs, never at enqueue.
- **The event stream is an optimisation, never a source of truth.** `GET /events` pushes a person's
  own conversation changes to their own open surfaces so two of them agree without polling. The bus is
  **in memory and per-user**: nothing is buffered for a stream that is not connected, and there is no
  replay or cursor — a reconnecting client re-reads the listing, which restates everything. Keep it
  that way. The moment a client has to *rely* on a frame arriving, the stream becomes a second source
  for state the endpoints already own. This is also why the only things travelling by value are the
  switches and a turn's verdict — each a single scalar the listing or the transcript restates on the
  next read — and a transcript never does.
- **One model-facing `search` tool, deterministic aggregator (no nested model calls).** Queries the
  local RAG index first; a hit ≥ `LocalMinScore` answers from docs, else falls back to Tavily, else
  an honest "nothing found". `web_search` is internal — the model only ever sees `search`. A web
  *failure* is "couldn't search," never "nothing exists" (the measured-or-unknown rule).
- **Fail-closed null adapters compose the graph.** `DisabledRetrieval` / `DisabledWebSearch` are
  registered by default; the real adapter registers *after* and wins only when configured. This is
  why the Service boots with nothing but a model server + kgsm.
- **RAG is producer/consumer coupled by one on-disk `.krag` file.** The indexer writes a *versioned*
  index (format version + embedding model + dimension + chunk params); a mismatch is **rejected on
  load** (a different embedder = a different vector space). The read path **hot-reloads** on atomic
  swap and degrades to last-good on a bad read.
- **Two client stacks by design.** Chat lives in `TheKrystalShip.Llm` (JIT); embeddings live in
  `TheKrystalShip.Rag` (Native-AOT). The indexer is a resident daemon on a VRAM-budgeted box, so it
  must be AOT — which forces the RAG core AOT-clean (source-generated JSON, zero reflection).
  Embeddings deliberately do **not** live on `ILlmClient`. Justified duplication, not an accident.
- **The inference server is one registration, and nothing above it knows which answered.**
  `Llm:Provider` picks Ollama or llama.cpp behind `ILlmClient`; `Rag:Provider` does the same behind
  `IEmbeddingClient`, independently. Both are read **once at startup** — a swap is a restart.
  **llama.cpp is the default**, on measurement: the same model at the same raw speed (38 tok/s
  either way), the KV-cache/batching/speculative controls Ollama chooses for you, and with the
  Gemma 4 draft head ~1.8x on structured output — which is what tool arguments and file bodies are.
  Switch either way with `deploy/llama-server/use-backend.sh`, which moves the units, the service's
  config AND the indexer's together; moving one alone points something at a dead port.
  The wire formats differ in ways that live entirely inside the two clients: llama.cpp streams a
  tool call's arguments as fragments (accumulated in `LlamaCppStreamParser`), addresses a tool
  result by call id rather than tool name (assigned per request in `LlamaCppRequestBuilder`), and
  fixes the context window at launch so `Llm:ContextWindow` is never sent to it — only used to
  stamp token accounting, and it must match the server's `-c`.
  ⚠ **llama-server needs `--jinja`.** Without it the `tools` array is accepted and no tool call is
  ever emitted: the assistant answers and silently never acts. With it, Gemma 4's own template and
  llama.cpp's native `PEG_GEMMA4` format handle tool calls under a constrained grammar — not the
  generic fallback. Ollama does *not* use that path at all (it runs llama-server with `--no-jinja
  --chat-template chatml` and parses tool calls in its own Go layer), so the two backends encode
  tool calls differently and a switch is a measurable change to routing. Units and the
  one-command switch: `deploy/llama-server/`.

- ⚠ **A GGUF lifted from Ollama's blob store is not a portable GGUF.** Ollama's embeddinggemma blob
  fails to load in mainline llama.cpp (`wrong number of tensors; expected 316, got 314`) while
  Ollama's own bundled server accepts it. Take embedding models from their published GGUF repo.
- ⚠ **The indexer is told its backend, never infers it.** It is a separate unit taking CLI flags
  (`--provider`, `--endpoint`), because Ollama and llama-server expose different embedding routes
  and guessing would trade a failed build for a wrong index.
- ⚠ **Changing the embedding backend does not rebuild the RAG index.** The indexer is incremental by
  content hash and the index header records the model *name*, which does not change when the server
  behind it does — so a switch reports "0 embedded, N reused" and keeps vectors from the previous
  embedder. Nothing detects it and retrieval degrades quietly. Delete the `.krag` and re-run the
  indexer, which `deploy/llama-server/use-backend.sh` warns about on every switch.

## Repo-specific invariants

- **Never shell out to `kgsm.sh` (or open the watchdog socket) from C#.** All engine access goes
  through **kgsm-lib**, consumed as a versioned `PackageReference`
  (`TheKrystalShip.KGSM.Lib`) resolved from the org's GitHub Packages feed — the same pattern
  kgsm-api, kgsm-bot and kgsm-monitor use. **The pinned version is what this repo compiles against,
  not the sibling checkout**: a capability added to kgsm-lib is invisible here until it is published
  and the pin is bumped. Need more kgsm data? Extend a kgsm-lib method (then
  repack + bump) or an assistant port.
- **Never fabricate a status or metric.** Measured, or explicitly "unknown" — never invented. The
  ecosystem-wide rule; it's also why the eval scores *trajectory* (which tool was called), never a
  world fact (`TheKrystalShip.Kgsm.Assistant.Eval/CLAUDE.md` invariant #1).
- **The rule covers the figures in the reply, not just the tool call.** A measured value can be right
  in the tool result and wrong in the sentence: `gemma4:12b` reports a port correctly one time in three
  when asked for "the port" of a server that has two, and states a plausible neighbour otherwise.
  `FabricatedFigureClaim` holds every run of 4+ digits in the reply against everything the turn was
  given (`MeasuredValues` — tool output, the request, the injected lists); unbacked figures re-prompt
  the turn once, quoting them back, and a second failure appends a correction that claims no correct
  value, because none is known. Four digits is the floor: measured values start there and the numbers
  a reply computes rather than copies stop below it. It runs only on a turn that called a tool — a
  turn that called none is answering from the model's own knowledge, where a well-known default port
  is a fair answer.
- **The rule covers the model's account of its own turn.** A reply is held against what the turn did:
  on a turn that staged nothing and ran nothing, a first-person claim of a staged or completed action
  is false by construction (`UnbackedActionClaim`). The check is one-sided — it never runs on a turn
  that staged or executed something, so it cannot contradict a real action; the auto-accept path
  records that it acted via `IConfirmationContext.NoteActionPerformed`. Offers ("I can stop it") and
  reports of the world ("it was restarted an hour ago") are honest and untouched. The claim is caught
  through `AgentTurn.ReviewReply`, the outbound counterpart of the per-call `ToolGate`: the first one
  re-prompts the turn (the model is told it called no tool, with the request restated) and only a
  second one is corrected and left standing, in the reply AND in the record.
- **A replayed turn carries the tool calls it made** (`ModelContextProjection`). The transcript is
  also the examples the model imitates: a turn that answered "start the server" by calling a tool,
  replayed as prose alone, teaches that the request is answered by *describing* the action — the next
  reply then narrates a staging that never happened (measured on `gemma4:12b`: reproducible on the
  very next turn). A past call's **output** is not replayed — a stale reading offered as current is a
  fabricated status — so each replayed call stands against a placeholder asking for a fresh call.
- **This leaf depends only on kgsm-lib + a local model server** and runs fully standalone — no other
  ecosystem service. Don't add a dependency on the API or a sibling leaf.
- **Authority is the ecosystem's ordered tier, and it comes from the KGSM account store.** A Discord
  login and a password login are answered from the same record, so a person holds the same tier here
  as in the Control Panel — both read that record rather than each deriving one. A guild role is a
  fact about a chat server and is not consulted anywhere in this service. Acting needs `operator`; reading another person's
  conversations needs `admin`. The store is the shared host file `/var/lib/kgsm/auth/users.db`
  (`Auth:UsersDbPath`), opened directly — a file cannot be down, which is what keeps this leaf
  standalone. This surface's own callback URL and scopes stay local, on `DiscordOAuth`.
- **A verified identity with no account here is provisioned unapproved, not denied.** It gets a real
  session holding `none`, so the chat can say "awaiting approval". `Auth:PendingUserCap` bounds that
  surface and `Auth:PendingUserTtlDays` expires what nobody looks at. A **disabled** account's live
  sessions stop being accepted outright, which is what makes disabling somebody in the Control Panel
  cut their sessions here with no call between the two services.
- **Nothing here talks to `discord.com` except `DiscordDirectory`**, and nothing mints or validates a
  session token except `TheKrystalShip.KGSM.Auth.Sessions`. Both come from `kgsm-auth`, shared with
  the API and the bot — a second implementation is how two surfaces come to disagree about who
  someone is. `AuthService` reaches them only through the neutral seams (`ISignInService` for the
  login, `IAuthorityProvider` for the tier), so it neither knows what a guild role is nor which
  provider answered. Authority is re-derived per request (cached for `Auth:RoleCacheTtlSeconds`,
  default 5), never read off the bearer, so a change made in the panel takes effect without a new
  sign-in.
- **The key sessions are signed with is stable whether or not anyone sets one.** `Auth:SigningKey` wins
  when it is set; blank means `HostSigningKey` generates 384 bits on the first start and keeps them in
  `/var/lib/kgsm-assistant/signing-key` (0600), reusing them forever after. Rotating either invalidates
  every issued token and signs everyone out.
- **`Directory.Build.props` carries a scoped NuGet-audit suppression** (one transitive SQLite
  advisory, no fixed version yet). It's deliberate and documented inline — don't widen it to
  `NuGetAudit=off`; delete it when a patched bundle ships.
- Work directly on **`main`** and commit there (the `kgsm-*` repos don't auto-branch).

## Where truth lives

| Doc | For |
|-----|-----|
| `docs/ARCHITECTURE.md` | The mental model: layers, agent turn (incl. the live-list-injection finding), ports/adapters, the `gemma4:12b` model choice, RAG split, ecosystem boundary |
| `docs/DEPLOYMENT.md` | Cold-start runbook (prereqs → build → publish → run → verify), incl. model-server/VRAM tuning |
| `docs/CONFIGURATION.md` | Every config section/key/default, env-var form (`Section__Key`), the secrets list |
| `docs/wire-contract.md` | The versioned public wire contract: the `/turn` stream, the `/confirm` channel, and what a client may rely on |
| Per-project `README.md` | Surface-specific usage (`.Cli`, `.Service`, `.Llm`, `.Rag.Indexer`) |
| `*.Eval/CLAUDE.md` | The benchmark's design integrity — read before changing how scoring works |

Config is layered (the settings file beside the binary < host file < `Section__Key` env < CLI
flags); the Service's is `kgsm-assistant.settings.json` and declares its whole surface, while the
CLI and Eval — interactive tools, not leaves — keep `appsettings.json`;
**secrets are environment-only** (`docs/CONFIGURATION.md`). The default model is `gemma4:12b`;
`Llm:ContextWindow` is a **fixed VRAM reservation**, not a ceiling.

## Version tracking

- **Version source:** `<Version>` in `TheKrystalShip.Kgsm.Assistant.Service/…csproj` — this repo's release line, the one `CHANGELOG.md` tracks and the one the assistant package ships under. The published libraries (`TheKrystalShip.Llm`, `.Kgsm.Assistant`, `.Kgsm.Assistant.Relay`) each version independently of it and of each other.
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version, `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.

## Documentation & comments: present-tense canon only

Prose in this repo — every doc, `README`/`CLAUDE.md` section, and in-code comment — describes
**how the thing works right now**, nothing else. History lives in the `CHANGELOG` and git
history; never duplicate it into docs or code.

- **No transitions.** Never "was X, now Y", "used to…", "changed from…", "no longer…", or any
  before/after framing. State the current rule flat: a sentence that only makes sense to a reader
  who knows what the code *used to* do is dead weight, because that "before" no longer exists
  anywhere in the code.
- **Tombstones leave no marker.** When something is removed — dying naturally as part of the work,
  or explicitly asked to be deleted — the removal is silent: no *"removed X"*, no *"X is gone"*,
  no *"deprecated, use Y instead"* pointing at a corpse. The prose reads as if it never was. Code
  kept while the thing that justified it was deleted gets a live present-tense reason to exist —
  or goes too.
- **No residue of the active work.** References only meaningful *during* a piece of work don't
  survive it: *"temporary shim for the rework"*, *"added to satisfy the new requirement"*,
  milestone/phase labels (*"per M2"*, *"the Phase 1 step"*). If a line's justification is the work
  that produced it rather than the system as it now stands, it goes.
- **No volatile numbers.** Counts and versions that drift — how many projects/files/tests/
  partials exist, a dependency's pinned version, a file's line count — never go in prose: they are
  stale the moment anything changes, and nothing fails to remind anyone. Name the authoritative
  source instead (the csproj, the directory, the barrel file). A number belongs in prose only when
  it *is* the contract (a port, a timeout, a cap) or a measured fact that is itself the reason a
  design exists.
- **Edits are replacements, not appends.** When changing an existing feature, rewrite the affected
  doc/comment fresh as if writing it for the first time — never append a correction under the
  stale version, and never leave the stale version standing beside the new. The current revision
  does not converse with prior revisions.

A reader six months from now should learn the system from the doc without knowing what it
replaced. If you catch yourself explaining a change, stop — that sentence belongs in the commit
message. When touching prose that already violates this, rewrite it to present-tense canon in
passing.
