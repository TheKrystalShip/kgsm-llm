# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-llm` is the **LLM / assistant** leaf of the KGSM ecosystem: a local, tool-calling AI
assistant (runs on a local **Ollama** model — no cloud LLM) that answers questions about, and with
authorization acts on, the game servers a `kgsm` engine manages. The workspace keystone is
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

- **Live-Ollama smoke tests are inert by default** — they no-op unless `KGSM_LIVE_OLLAMA=1` (and
  some need a kgsm host / pulled models). The default `dotnet test` is fully hermetic:
  `KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~CliLiveSmokeTests`.
- **`TheKrystalShip.Rag*` must publish Native-AOT-clean** — expect **0 ILC warnings**:
  `dotnet publish TheKrystalShip.Rag.Indexer -c Release -r linux-x64`.

Running the deployables (each is a thin host over the same backend; needs a reachable Ollama, and
the assistant surfaces also need `KGSM__Path` → a real `kgsm.sh`):

```bash
# CLI — one-shot / pipe / REPL
KGSM__Path=/usr/local/bin/kgsm dotnet run -c Release --project TheKrystalShip.Kgsm.Assistant.Cli -- "what's installed?"
# Service — HTTP/SSE; /health needs no secrets
dotnet run --project TheKrystalShip.Kgsm.Assistant.Service   # then curl http://127.0.0.1:5180/health
# Eval — reproducible benchmark (live run needs Ollama; routing mode also needs a kgsm host)
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

- **`TheKrystalShip.Llm`** — generic Ollama tool-calling **agent loop**. Knows nothing about KGSM;
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
- **Inject the live instance/blueprint list into the system prompt every turn.** This is
  load-bearing, not cosmetic — without it the model wastes a round-trip calling `list_instances` to
  "ground" itself before acting (a measured finding; see `kgsm-llm.md §7a`). The prompt is rebuilt
  fresh each turn for this reason.
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
  why the Service boots with nothing but Ollama + kgsm.
- **RAG is producer/consumer coupled by one on-disk `.krag` file.** The indexer writes a *versioned*
  index (format version + embedding model + dimension + chunk params); a mismatch is **rejected on
  load** (a different embedder = a different vector space). The read path **hot-reloads** on atomic
  swap and degrades to last-good on a bad read.
- **Two Ollama clients by design.** Chat lives in `TheKrystalShip.Llm` (JIT); embeddings live in
  `TheKrystalShip.Rag` (Native-AOT). The indexer is a resident daemon on a VRAM-budgeted box, so it
  must be AOT — which forces the RAG core AOT-clean (source-generated JSON, zero reflection).
  Embeddings deliberately do **not** live on `ILlmClient`. Justified duplication, not an accident.

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
- **The rule covers the model's account of its own turn.** A reply is held against what the turn did:
  on a turn that staged nothing and ran nothing, a first-person claim of a staged or completed action
  is false by construction, and `ServerAssistant` appends a correction (`UnbackedActionClaim`). The
  check is one-sided — it never runs on a turn that staged or executed something, so it cannot
  contradict a real action; the auto-accept path records that it acted via
  `IConfirmationContext.NoteActionPerformed`. Offers ("I can stop it") and reports of the world ("it
  was restarted an hour ago") are honest and untouched.
- **This leaf depends only on kgsm-lib + a local Ollama** and runs fully standalone — no other
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
- **`Auth:SigningKey` must be stable on any real host.** Unset means a per-process key: every restart
  invalidates every issued token and signs everyone out.
- **`Directory.Build.props` carries a scoped NuGet-audit suppression** (one transitive SQLite
  advisory, no fixed version yet). It's deliberate and documented inline — don't widen it to
  `NuGetAudit=off`; delete it when a patched bundle ships.
- Work directly on **`main`** and commit there (the `kgsm-*` repos don't auto-branch).

## Where truth lives

| Doc | For |
|-----|-----|
| `docs/ARCHITECTURE.md` | The mental model: layers, agent turn, ports/adapters, RAG split, ecosystem boundary |
| `docs/DEPLOYMENT.md` | Cold-start runbook (prereqs → build → publish → run → verify), incl. Ollama/VRAM tuning |
| `docs/CONFIGURATION.md` | Every config section/key/default, env-var form (`Section__Key`), the secrets list |
| `docs/wire-contract.md` | The versioned public wire contract: the `/turn` stream, the `/confirm` channel, and what a client may rely on |
| Per-project `README.md` | Surface-specific usage (`.Cli`, `.Service`, `.Llm`, `.Rag.Indexer`) |
| `*.Eval/CLAUDE.md` | The benchmark's design integrity — read before changing how scoring works |
| `kgsm-llm.md` | **Historical** handoff (2026-06-08). Trust it only for GPU/VRAM tuning + the `gemma4:12b` bake-off + the live-list-injection finding; its status sections are stale |

Config is layered (the settings file beside the binary < host file < `Section__Key` env < CLI
flags); the Service's is `kgsm-assistant.settings.json` and declares its whole surface, while the
CLI and Eval — interactive tools, not leaves — keep `appsettings.json`;
**secrets are environment-only** (`docs/CONFIGURATION.md`). The default model is `gemma4:12b`;
`Ollama:NumCtx` is a **fixed VRAM reservation**, not a ceiling.

## Version tracking

- **Version source:** `<Version>` in `TheKrystalShip.Llm/TheKrystalShip.Llm.csproj` and `TheKrystalShip.Kgsm.Assistant/TheKrystalShip.Kgsm.Assistant.csproj` (each versioned independently)
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
