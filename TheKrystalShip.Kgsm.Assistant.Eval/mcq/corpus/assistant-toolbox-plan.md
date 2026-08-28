# Assistant Toolbox — Design Plan

> **Status:** Design agreed, pre-implementation · **Last updated:** 2026-06-11
> **Companion doc:** [`architecture.html`](./architecture.html) (Control Panel front-end architecture, Proposal v0.2 — §5 is the assistant)
> **Code:** `TheKrystalShip.Kgsm.Assistant` (library), `*.Service` (HTTP host), `TheKrystalShip.Llm` (Ollama client + agent loop)

This file is the source of truth for **how the KGSM assistant's tools are designed**. `architecture.html` describes what the *website* wants; this file reconciles that with the shared backend reality (one LLM serves both the website and the Discord bot) and records the decisions we've made.

---

## 1 · The shift (from architecture.html §5)

The assistant is no longer a client-side exception. It is a server-owned domain like every other:

- **Backend-mediated, surface-agnostic.** One LLM, one system prompt, one tool catalog — all server-owned. The website and the Discord bot are *renderers* of the same typed event stream. The model has no concept of a "card."
- **A turn is an SSE stream** (`POST /assistant/turn` → `text/event-stream`) interleaving `text.delta` / `tool.start` / `tool.result` / `command.proposed` / `command.verified` / `error` / `done`.
- **Tool results are semantic, not presentational.** Every result is a `ToolResult<K,D>` envelope — `{ tool, confidence, subject, links, summary, data }`. No colours, icons, or routes. Each surface maps semantics → its medium.
- **Commands are requests, not executions.** The model calls a command tool → backend *gates* admissibility → emits `command.proposed` → **human** confirms → the *surface* issues `POST /servers/{id}/commands` → backend verifies → pushes `command.verified`. The model never runs anything.

---

## 2 · Current state (what exists today)

> **Updated 2026-06-13 (Phase 1a).** The read-path catalog below has been refactored:
> the per-instance `list_instances`/`get_server_status`/`is_server_active` tools are
> replaced by one merged **`get_status`** (omit `instance_name` → bulk fleet read via
> `GetAllStatuses`, fixing the MaxIterations cap), **`view_config_file`** is added as an
> authorized-only, path-bound, redacted read, and a no-op `IToolRelevanceFilter` seam
> (§3.2) is in place. The mutating/destructive tiers are unchanged (still inline /
> staged — the §3.5 propose-only generalisation + §5a typed events are Phase 1b). The
> table below is the pre-Phase-1a baseline.

**Tools** (`LlmTools.cs`, 11 total, organised by risk tier):

| Tier | Tools |
|---|---|
| Read | `list_instances`, `list_blueprints`, `get_server_status`, `is_server_active` |
| Mutating (run inline) | `start_server`, `stop_server`, `restart_server`, `create_backup`, `update_server` |
| Destructive (staged for human confirm) | `install_server`, `uninstall_server` |

**Ports** (the only data sources wired up today):
- `IServerInventory` — instances map (name → game), blueprint names.
- `IServerOperations` — start/stop/restart/backup/update, status text, is-active, install, uninstall.

There is **no port** for metrics, console/logs, config, host diagnostics, network/ports, audit, or change-timeline. The architecture doc's authority line is "KGSM **+ monitor**" — half the diagnostic data the website wants does not exist in KGSM yet.

**Operating constraints:**
- The LLM is a **local Ollama model on an RTX 3060** that must stay 100% in VRAM (CPU/RAM are reserved for game servers). Small model → weak at tool selection from a large or semantically-overlapping catalog. This drives most of the design below.
- Neither the website backend nor the Discord bot is operational. **We can break and rewrite freely.** Nothing must stay running.

---

## 3 · Design decisions

### 3.0 Governing principle (overrides the rest of §3) — *locked 2026-06-11*
**The LLM is a router + narrator, never a reasoner.** Any inference the deterministic layer can do, it does; the model only (a) selects a tool from intent and (b) renders pre-computed facts in natural language. The model never *produces* a fact — it surfaces one. Every decision below is an instance of this: aggregators compose deterministically (§3.4), `trace_root_cause` infers via a rules table not a sub-call (§3.4 / §7·Q1), the catalog stays coarse so selection is reliable (§3.2), and unknowns are typed at the source so the model can't mistake a gap for data (§3.7). When a future choice is ambiguous, the tie-breaker is fixed: **push it into determinism; leave the model the narration.**

### 3.1 One server-owned toolbox, shared across surfaces
A single backend toolbox holds all tools. The same catalog serves the website and Discord because tools are **data-only / consumer-agnostic** (see §6). Default policy: expose every tool the caller is authorised for.

### 3.2 Two independent tool-subsetting axes
"Give the model all the tools" is filtered by two *orthogonal* mechanisms — don't conflate them:

1. **Authorization (mandatory, security).** A caller only ever sees tools their permission tier allows. **Login is Discord-only on both surfaces**, so the principal and its permissions are *identical* regardless of origin — the gating pipeline needs no per-surface work. (Keep *surface* available as a future **policy** input — e.g. allow a destructive confirm on the website's two-step button but not a Discord reaction — even though identity is uniform.)
2. **Relevance / reliability (optimization).** If the authorised catalog is too large for the local model to select reliably, narrow it to the tools relevant to the request *before* the prompt hits the LLM.

> **"Fits in the context window" is the wrong trigger for axis 2.** Token capacity is not the binding constraint — small-model selection *accuracy* degrades first, especially among semantically overlapping tools. Assume the relevance filter is needed; build the **seam** now (`GetToolsFor(context)` returning all by default), even if the first implementation is a no-op. Retrofitting a filter after the dispatcher assumes "all tools always present" is the expensive path.

> **Locked 2026-06-11 — the seam stays a no-op; coarse + bulk tools are the actual fix.** The MaxIterations=8 failure is a tool-*granularity* problem, not a too-many-tools problem, so we narrow the catalog by making tools **coarse (aggregators) and bulk (`get_all_statuses`)**, not by filtering. The two relevance-filter *mechanisms* — keyword/intent routing and an embedding model — are **rejected**: routing fails silently on phrasing it didn't anticipate, and a second model fights for the VRAM reserved for game servers. Both add a failure point to buy what coarse tools already buy. Keep `GetToolsFor(context)` as the seam (returning all), but treat the no-op as the **decision**, not a placeholder — only revisit if a coarse catalog still overwhelms selection under test.

### 3.3 Two-layer architecture: capabilities vs tools
Separate **what data we can get** from **what the model can call**:

```
model-facing tools:   get_console   get_network   run_health_check        trace_root_cause
                          │              │            │  │  │  │            │  │  │  │
                          ▼              ▼            ▼  ▼  ▼  ▼            ▼  ▼  ▼  ▼
capabilities (ports): GetLogs()      GetPorts()   GetLogs GetPorts        GetLogs GetAudit
                          │              │         GetDisk GetConfig       GetNet  GetMetrics
                          ▼              ▼              (same fns, reused)
data sources:         KGSM / monitor / host
```

- **Layer 1 — capabilities (ports):** plain backend functions that fetch/produce data. No LLM involved.
- **Layer 2 — tools:** thin adapters exposing a capability (or a composition of them) to the model with name/description/params, producing a `ToolResult` envelope.

This maps onto the existing code: `Ports/*` are capabilities, `ToolDispatcher` holds the handlers. An aggregator is just a handler that calls several ports.

### 3.4 Aggregator tools = deterministic composition over shared capabilities
Some website tools are aggregators: `run_health_check` and `trace_root_cause` pull from several data sources and synthesise one result.

> **SHIPPED 2026-06-14 (`run_health_check` — the first aggregator + the `ToolResult<K,D>` envelope).**
> V1 checks: **liveness · log-error scan · update-available · host disk headroom** (ports-reachability
> and config-sanity deferred to V1.1 — NetworkService returns only raw text/exit-codes, config-sanity
> has no concrete rule yet). The reusable pieces landed: the `ToolResult<K,D>` envelope + `CheckState`
> taxonomy (in the assistant lib's `Envelope/`, NOT the generic LLM package — the loop stays
> domain-agnostic; folder is `Envelope` not `Results` to avoid colliding with ASP.NET's `Results`),
> and a **pure-function** `HealthCheckAggregator` (the single home for "what counts as healthy" — both
> surfaces only fetch+map a neutral `InstanceHealthSnapshot`, so the error tally can't drift).
> **Synthesis rule that bit (§D5):** KGSM is stateless → a deliberately **stopped** server is reported
> as info, never a failure; the log scan **skips** when stopped; an unknown update status / unreadable
> host disk **skips** (never a fabricated pass). `run_health_check` is a **ReadOnly** tool (verified:
> `get_status` already feeds the model recent logs, so the error excerpts are the same exposure class).
> **Live on Discord now (D3):** the bot got a structured read path (MediatR `GetHealthSnapshotQuery`
> → Infra over kgsm-lib) so it narrates real health text; the rich embed is deferred. **`.Data` is
> built + tested but not yet transported** — no surface consumes it (Discord narrates text); when the
> web card lands, the clean transport is enriching the loop's tool-result to carry an opaque card.
> Also added kgsm-lib `SystemInfo` (the `system info --json` host model — disk is `df -h` strings, e.g.
> `use_percent:"26%"`, not byte counts). Verified: kgsm-lib **443** + AOT 0-warning; kgsm-llm **27+76+93**;
> kgsm-bot **40**; live — both local models (gemma4:12b, qwen3.5:9b) select `run_health_check` and
> narrate the deterministic summary; the stopped path proven end-to-end on factorio-test. Coarse V1
> `LooksLikeError` (substring match) is an accepted tradeoff; format-aware tally via `LogParser` is V1.1.

- **They reuse the shared capability function, NOT the LLM tool loop.** `run_health_check`'s handler calls `GetLogs()` + `GetPorts()` + `GetDisk()` + `GetConfig()` directly and synthesises. **No nested model calls** — that would be slow, non-deterministic, and unreliable on the local model.
- **The two named aggregators are different difficulty — decide synthesis strategy per tool:**
  - `run_health_check` — *mechanical*: run N checks, rank by severity. Deterministic, clearly right.
  - `trace_root_cause` — *aggregation + causal inference*: the "why" is the product, not the fetch. **Deterministic only — locked 2026-06-11.** A timeline + a rules table of known KGSM failure signatures (port conflict → won't bind; disk full → backup/update fails; update landed mid-run → crash loop; unit inactive but management-script alive → split-brain). The model narrates the deterministic chain; it never authors it. **The "scoped LLM sub-call" fallback is retired** — causal inference on the local model is exactly the plausible-but-wrong failure we scrapped `kgsm-api` for. If the rules are too thin to assert cause, the honest output is a correlation at `confidence: possible`, not a guessed cause.
- **Not every capability must be model-facing.** Expose coarse aggregators as the model's default reach; keep fine-grained primitives as internal-only capabilities where exposing them just adds selection confusion. This is the *fix* for the website's overlapping-tool problem.
- **Aggregator results embed nested sub-results.** One model tool-call → one `summary` to ground the model + a rich multi-section card for the surface. Strictly better than the model firing 5 separate calls.
- **Design for partial failure.** Aggregators fan out to several sources (some shelling out to KGSM/host on a box also running game servers). Use parallel fetch, per-source timeouts, and graceful degradation — a timed-out source returns `skip`/`unknown`, the aggregate still returns. The envelope's `CheckState = pass|warn|fail|skip` already supports this.
- **Keep the graph shallow and acyclic:** aggregators → capabilities → data sources. No aggregator→aggregator chains.

### 3.5 Keep the security model; generalise propose-only
The existing risk-tier + propose/confirm staging is correct and the doc reaffirms it — re-home it on rewrite, don't discard it. One change: **commands never execute in the agent loop.** The model *requests* → backend gates → human confirms → surface issues the REST command. This retires the inline-mutating tier (start/stop/restart/update fold into the same staging the Destructive tier already uses).

> **SHIPPED 2026-06-13 (Phase 1b — command path).** The inline-mutating tier is gone: `start_server`/`stop_server`/`restart_server`/`create_backup`/`update_server` are now **propose-only** like install/uninstall. (Those five were later **consolidated into a single `server_command` verb tool** on 2026-06-14 — see the SHIPPED note below; this Phase-1b note describes the state when they were five separate tools.) The dispatcher `StageCommandAsync` resolves + stages every command into the `IConfirmationContext`; the single-instance `IServerOperations` op runs only from `ServerAssistant.ConfirmAsync` after a human confirms (`ToolDispatcher.cs`, `ServerAssistant.cs`). The two old caps (mutating=5 + destructive-staged=3) collapse to **one `MaxStagedCommandsPerMessage`=5** spanning every command kind. **Risk tier = confirmation friction now**, uniform for V1 (Confirm/Cancel for all, Q5); `ConfirmationKinds.Destructive` marks uninstall as the home for the deferred type-the-name friction. Cross-repo lockstep: `ConfirmationKind` gained `Start/Stop/Restart/Update/Backup` (appended — the Service token serialises `(int)Kind`), and **`kgsm-bot` landed in step** (`ConfirmationIds` encodes the new kinds under Discord's 100-char customId; `ConfirmationModule` confirms them via the existing MediatR commands). Verified: kgsm-llm 155 + kgsm-bot 20 offline tests; gated live test `CommandPrompt_StagesConfirmation_NeverExecutesInLoop` (both local models stage a Start, never execute inline). **Still deferred (own designs):** `open_ports` (UFW/UPnP — entangled with the watchdog net-delegation lane) and `edit_config_file` (§3.8 — needs a per-instance config-validation home, a write port, and a server-side staged-op store since its payload can't fit a Discord customId).
>
> **SHIPPED 2026-06-14 (`server_command` consolidation — §4.1).** The five lifecycle tools (`start_server`/`stop_server`/`restart_server`/`update_server`/`create_backup`) collapsed into ONE model-facing **`server_command`** with a `verb` parameter (`start|stop|restart|update|backup`) — the locked §4.1 shape, realizing §3.2's small-model selection win (fewer, less-overlapping choices). `install_server`/`uninstall_server`/`set_config_value` stay separate (distinct params/confirm tiers). **Zero new capabilities:** verb→`ConfirmationKind` routing in `ToolDispatcher.StageServerCommandAsync` (single source of truth `LlmTools.ServerCommandKind`/`ServerCommandVerbs`); the gate (name-set), confirm path (`ConfirmationKind`), Service tokens/SSE, and bot `ConfirmationIds`/`ConfirmationModule` are **untouched** — no cross-repo ripple (bot rebuilt clean, 36 tests green). Also added an optional JSON-schema **`enum`** constraint to the generic `LlmToolParameter.AllowedValues` (the Ollama client emits it) so the model is steered to a valid verb — the reliability lever, not just a description. Verified: kgsm-llm offline **27 + 61 + 85** green (+3 generic serializer tests, +2 invalid-verb cases), and the gated live test stages **start + restart** via `server_command` across **both** local models (gemma4:12b, qwen3.5:9b). On branch `feat/instance-config-editing` working tree — **not committed** (belongs on its own branch when the user asks).

### 3.6 The model is a consumer too
The result envelope serves two readers: the **surface** (full structured `data` → card) and the **model** (reads the tool result in the agent loop to narrate). Don't feed the model the full card payload — it bloats context on the exact model we're keeping lean. Split it: the model's context gets `summary` + a few key facts; the surface gets full `data` out-of-band via the SSE `tool.result`. Elevate `summary` from "fallback" to **the model's grounding text** — a first-class field.

> **Locked 2026-06-11 — `summary` is written by the deterministic layer, and a single-source read may need no narration at all.** Because the model never produces facts (§3.0), `summary` is *authored by the backend* from authoritative data — the model at most paraphrases it, never generates it. For a single-tool read (e.g. `get_server_status`), the surface already holds the full card via `tool.result`; the model narrating over it adds latency and a hallucination surface for nothing, so the model may **stay silent and let the card speak**. Model narration earns its keep only on **multi-source synthesis** (e.g. "why is it slow") and conversational framing — not on echoing a card the surface already renders. The deterministic `summary`-writer must never coerce an unknown into a value (§3.7).

### 3.7 Typed unknowns — `Reading<T>` (capability layer) — *locked 2026-06-11*
The `--fast` → `updates_available:false` bug (§11.2) was a *category*, not a one-off: **a default value that is type-identical to a real measurement.** `false` reads as "checked, no update"; `0` as "measured zero"; `""` as "known empty." In each case the consumer — model *or* surface — cannot tell *measured* from *we-had-nothing-so-we-filled-the-slot*. The rule: **a capability that didn't measure something returns a value structurally distinguishable from any real measurement — never a masquerading default.**

**Decision: a `Reading<T>` wrapper, decided at the capability layer.** *(Implemented 2026-06-13 — see the §11.6 SHIPPED note.)*
```ts
type ReadingState = "measured" | "unsupported" | "unavailable" | "skipped";
type ReadingCode  = "requires_regeneration" | "deadline_exceeded" | "monitor_offline" | "source_error";
interface Reading<T> { state: ReadingState; value?: T; reason?: string; code?: ReadingCode; }
```
- **measured** — real value present.
- **unsupported** — *permanent* absence (this game has no player-query protocol). Surface renders "N/A"; model says "not available for this game type."
- **unavailable** — *attempted, produced nothing* (source down, monitor offline, or the fetch hit its deadline). Surface offers retry; model says "couldn't read it just now."
- **skipped** — *never attempted* — we chose not to run it (`--fast`; a brief tier that doesn't fetch this field).

**The discriminator is "did we attempt the fetch?"** `skipped` = never started; `unavailable` = started but came back empty. **A timeout is always `unavailable`, never `skipped`** — "deadline vs down" is a `code`, not a state. `code` exists *only* to subdivide `unavailable`/`skipped`, never to echo `state` (there is no `unsupported_game` code — that's just `state: unsupported`); in C# it is a **typed enum, never a bare string** (a stringly-typed code is the exact silent-drift hole this type exists to close).

*Why the wrapper over plain nullables:* `null` is one bit — it says *that* we don't know, never *why*, and "we'll never know" vs "try again in a second" is a real UX **and** narration difference. **State is decided where the data is** — the capability/port is the only thing that knows whether it measured; the envelope just carries it up. Cost: each capability return type carries it, and each concrete `Reading<T>` needs a `KgsmJsonContext` registration (Native-AOT, reflection-free).

**One wrapper, every granularity — this also retires the bulk-error shape.** `Reading<T>` nests: a *whole-instance* read failure is `Reading<InstanceStatus>` at `state: unavailable, code: requires_regeneration`, and *inside* a successful status a missing player count is `Reading<int>`. So the per-instance bulk-error element `{ error, instance, requires_regeneration }` (§11.2a/§11.6) is **retired** — the bulk read returns `Dictionary<string, Reading<InstanceRuntimeStatus>>`, replacing the ad-hoc nullable `Error`/`RequiresRegeneration` pair. KGSM's bash wire format does **not** change; the lib's converter maps KGSM's two on-wire shapes (status object \| error object) into the one `Reading<T>`. (kgsm-lib refactor tracked in §11.6 — zero consumers today, so it must land *before* the first toolbox tool wires `GetAllStatuses` in.)

**Where it bites in this system** (landmines already in the plan): `players.current/max` (§8 — `unsupported` for a no-query game, `unavailable` on a failed query, never `0`); per-instance CPU/RAM when the monitor is down or a source times out (§3.4 — `unavailable`, never `0%`); split-brain liveness (§11.1a — `unavailable`/conflicting, not a silent pick); aggregator coverage (`run_health_check` reports "4 of 5, 1 unavailable", never 4/4 green).

**Two orthogonalities, so the absence-enums can't drift:**
- **vs `confidence` (§5)** — `confidence` (`confirmed|likely|possible`) is trust in an *inference*; `Reading.state` is *presence of a measurement*. A field can be `measured` while the aggregate verdict is `possible`, and vice versa — separate axes.
- **vs `CheckState` (§3.4/§5)** — `CheckState` (`pass|warn|fail|skip`) is an aggregator check's pass/fail *judgment*; `Reading.state` is field-level presence. They map, they don't merge: a source whose `Reading` is `unavailable` **or** `skipped` makes that check `CheckState = skip` (no judgment rendered) — but `CheckState` also carries `pass|warn|fail`, which `Reading` has no notion of.

### 3.8 Config access — `view_config_file` / `edit_config_file` (gated, path-bound) — *locked 2026-06-12*
The assistant must inspect and help fix server configs (high-value — the owner's Discord users fear configs). Resolution = **two raw, path-bounded tools, NOT a structured per-key API.** The per-game structured-schema approach (N games × N formats × version drift) is **rejected as unmaintainable** (owner decision — it's why nobody ships it). The LLM is genuinely capable at ini/json/yaml/xml string replacement; the residual risk is not capability but *recovery* on the weak local model, so safety lives in the **envelope**, not in constraining edits to known keys.

**Two tools** (collapsed from an earlier 4-tool structured+raw split — owner simplification; fewer tools = less small-model selection tax, §3.2):
- `view_config_file(server_id, file)` — read, redacted.
- `edit_config_file(server_id, file, old_string, new_string)` — **propose-only command** (a `Verb` on `POST /servers/{id}/commands`; config edits operate on an *existing* `{id}`, unlike `install`). **Anchored replacement, not whole-file rewrite.** (V1 confirm = **Confirm/Cancel button**; the diff-review affordance is deferred — Q5.)

**The envelope (what makes the human-approval gate trustworthy on a weak model):**
1. **Path-bound to the instance's install dir** (KGSM provides it via the inventory). Enforced on the **canonicalized real path** (`realpath` + prefix check *after* resolution; reject any symlink/`..` that escapes) — a string-prefix check is bypassable. The *system* guarantees **location**; the *human* judges **content** (a non-technical user won't notice a bad path).
2. **Anchored replacement** — `old_string` must match **uniquely**; a bad anchor is rejected before any human sees a proposal. Bounds the blast radius to the changed lines and gives the human an exact diff to ✓.
3. **Snapshot → apply → validate → rollback-on-fail.** Snapshot = a **cheap file-level copy** (`file`→`.bak`, or hold the original bytes in the backend) — **NOT** KGSM's `create_backup` (that's a heavy whole-instance/game-data backup; wrong mechanism for a one-line config edit). Apply, validate, and on failure restore the snapshot → `command.failed`. **Validation is format-aware, and for V1 it is the load-bearing net — not generic well-formedness:**
   - **V1 / INI** — generic "does it still parse" is **near-toothless** (INI is so permissive that a typo'd key like `maxplayers` for `max_players` silently orphans the original and still "parses"). So V1 validation = **read the file back through `ConfigService`, confirm the edited key still exists and is well-typed** against KGSM's known key semantics. **Mandatory for V1, not optional** — re-parse-for-syntax buys ~nothing here, and the owner's non-technical users will *not* catch `maxplayers` in a diff.
   - **V2 / XML·YAML·JSON** — structural re-parse *does* catch the "model emitted invalid markup" class (closing tags, indentation, quoting), so it's the primary net there.
   This validation is a **backend behavior of `edit_config_file`, not a separate tool** — collapsing to 2 tools drops the structured *tool*, not the structured *check* where the schema is cheap.
4. **Secret redaction** in whatever is echoed to the channel/model — light for V1 (the KGSM `.config.ini` carries few secrets), essential at V2 (game configs hold RCON passwords / tokens). Deferred wrinkle: redaction fights *editing* a redacted secret value (can't anchor on it) — handle when game configs land.

**V1 scope = the KGSM `.config.ini` only.** The "which files the model may see/edit" whitelist starts as `{.config.ini}`. **Expanding to other server config files is a whitelist change, not a new tool** — the whole point of the 2-tool + path-whitelist shape: granular, auditable control over where the tool operates, with V1→V2 as config, not code.

**Relation to `get_config` (internal cap, §4.1) — unchanged, NOT promoted.** `get_config` stays the **structured, internal** read that deterministic aggregators (`run_health_check`) use to *detect* bad config; `view_config_file` is the **raw, model-facing** read for show + propose-fix. Different consumers, both kept. The detect→fix flow survives: an aggregator flags a bad value → the assistant offers a one-tap `edit_config_file` proposal.

---

## 4 · The 12 tools the website proposes (architecture.html §5·b)

| Tool | Params | Card | Role | Notes |
|---|---|---|---|---|
| `get_server_status` | `server_id` | — | cardless read | status, players, version, ip. **Exists today.** |
| `get_audit_log` | `server_id, window` | — | cardless read | recent operational events (grounds correlation) |
| `get_performance` | `server_id, window` | performance | **internal cap** | metrics + anomaly · *internal-only (locked 2026-06-11)* — LLM never sees it |
| `get_console` | `server_id, lines` | console | **internal cap** | flagged log lines · *internal-only* |
| `get_config` | `server_id` | config | **internal cap** | config values · *internal-only* |
| `get_host_diagnostics` | `server_id` | host | **internal cap** | disk / swap / neighbours / zombies · *internal-only* |
| `get_network` | `server_id` | network | read | required-vs-open ports + traffic |
| `run_health_check` | `server_id` | health | **aggregator** | full sweep → ranked checks |
| `trace_root_cause` | `server_id` | rootcause | **aggregator + inference** | causal chain across sources |
| `get_change_timeline` | `server_id, range` | changes | read | "what changed" window |
| `server_command` | `server_id, verb` | — | command request | start \| stop \| restart \| update (propose-only) |
| `open_ports` | `server_id` | — | command request | request firewall fix for closed ports (propose-only) |

Only `get_server_status` overlaps the current catalog by name. `server_command` absorbs the four current mutating tools.

> **Locked 2026-06-11 — model-facing catalog cut 12 → 8.** `get_performance`, `get_console`, `get_config`, `get_host_diagnostics` become **internal-only capabilities** (Layer-1 functions per §3.3) — plain deterministic, reusable methods the LLM never sees. They are reached only by aggregators (`run_health_check`) and by the **surfaces' own cards** directly (the website's console/config panels don't need the assistant to fetch them). Model-facing survivors: `get_server_status`, `get_audit_log`, `get_network`, `run_health_check`, `trace_root_cause`, `get_change_timeline`, `server_command`, `open_ports`. Rationale (§3.0/§3.4): expose coarse reach, keep fine subsystem-fetchers internal so the small model faces fewer, less-overlapping choices. **Implication accepted:** a direct "show me the raw logs/config" ask is served by the surface's card, not a dedicated LLM tool.

**Scope note → resolved (Q3, locked 2026-06-11).** The website list drops `install`, `uninstall`, `list_blueprints`, `create_backup`, `list_instances`, `is_server_active`. Resolution = **a shared superset of *capabilities*, uniform catalog, realized by coarsening — NOT a tool-count superset.** (The §3.2 no-filter lock makes the whole authorized catalog the per-turn selection surface, so raw tool count is a permanent tax — the original "they're just extra tools" lean under-weighted that.) Per-tool disposition:
- `is_server_active` — **not model-facing** (it *is* the MaxIterations=8 cause); internal cap, answered by the bulk read.
- `list_instances` — **not model-facing**; subsumed by `get_all_statuses`. Stays an internal cap for cheap enumeration.
- `create_backup` — **folded into `server_command` as a `backup` verb** (non-destructive op on an existing instance). +0 tools.
- `list_blueprints` — **model-facing read** ("what can I install/run?"; precursor to install).
- `install` / `uninstall` — **model-facing, kept** (owner decision: install is the *highest-frequency* Discord request and additive/low-risk; the assistant must help when the owner is away). **Asymmetric — not a symmetric verb-pair:** two tools, `install_server(blueprint, …)` (create-additive, **standard confirm**) and `uninstall_server(server_id)` (destructive — **V1: Confirm/Cancel button**; the stronger type-the-name confirm is deferred, Q5) — different params, different tiers. The propose→confirm gate (§3.0/§3.5) keeps it safe: the model only ever *proposes*; a human confirms and the surface issues the REST job. (Risk tier now = confirmation *friction*, since §3.5 already made every command propose-only.) **Review-flagged consequences (2026-06-12):** (a) *create vs operate* — `uninstall` rides the existing `POST /servers/{id}/commands` path (a `Verb`), but `install` has **no `id` yet** (blueprint → a *new* server), so it does **not** fit that contract and is **excluded from the §5 `Verb` union** — it needs its own **create/provision command** (e.g. `POST /servers` with a blueprint body → returns the new `id` + job). (b) *Discord destructive-confirm — V1 = Confirm/Cancel button (Q5)* — the stronger type-the-name confirm (a typed-name can't ride a Discord reaction — §3.2) is **deferred post-V1**, not required for launch.

**Net model-facing catalog = 12 (locked 2026-06-12; see §4.1).** Single + fleet status merged into one `get_status(server_id?)`; `get_network` moved internal (Q2); the config pair (`view_config_file` + `edit_config_file`, §3.8) added; `backup` is a `server_command` verb, not a tool. Count is up, but the additions are *semantically distinct* (provisioning vs diagnostics) and §3.2's selection tax bites worst among *overlapping* tools — so real cost < count. The relevance no-op seam (§3.2) is the escape hatch if testing says otherwise.

**Surface-scoping rejected.** The catalog stays uniform; surfaces differ only in how they *render* a proposal (the web Control Panel may choose not to surface a given `command.proposed` as a button). `surface` remains the reserved future *policy* input (§3.2), not built now — a static per-surface tool list would be the §3.2 relevance-filter in a costume, reintroducing divergence + a per-surface branch for a ~2-tool difference.

### 4.1 · Consolidated catalog (source of truth — supersedes the §4 proposal table above)

The §4 table is the website's original *ask*; this is the *locked* catalog after §3.0/§3.2/§3.4/Q3.

**Model-facing** (the model selects from these every turn — no relevance filter, §3.2):

| Tool | Kind | Confirm tier | Notes |
|---|---|---|---|
| `get_status` | read | — | **merged single + fleet (locked 2026-06-12)** — omit `server_id` → brief fleet read (the MaxIterations=8 fix, §11.6); with `server_id` → single detailed. Replaces `get_server_status` + `get_all_statuses` |
| `get_audit_log` | read | — | event history (needs persistence — §8) |
| `run_health_check` | aggregator | — | composes the **5** internal caps deterministically (§3.4) |
| `trace_root_cause` | aggregator | — | deterministic timeline + rules; no LLM sub-call (§3.4) |
| `get_change_timeline` | read | — | "what changed" window |
| `list_blueprints` | read | — | "what can I install/run" |
| `server_command` | command | propose-only | verbs: start \| stop \| restart \| update \| **backup** |
| `install_server` | **create** command | standard confirm | blueprint → *new* instance; **own create endpoint, not `/{id}`** (§5 `Verb` excludes it) |
| `uninstall_server` | command | **V1: Confirm/Cancel** | destructive; type-the-name confirm deferred (**Q5**) |
| `open_ports` | command | propose-only | firewall fix |
| `view_config_file` | read | — | raw + redacted; **path-bound to instance install dir** (realpath-enforced); V1 = `.config.ini` only (§3.8) |
| `edit_config_file` | command | propose-only · **V1: Confirm/Cancel** | anchored replace; `.bak` snapshot → validate → rollback; diff-review deferred (Q5); V1 = `.config.ini` (§3.8) |

**Internal capabilities** (Layer-1, §3.3 — the model never sees these; reached by aggregators and the surfaces' own cards): `get_performance`, `get_console`, `get_config`, `get_host_diagnostics`, **`get_network`** (Q2-resolved 2026-06-12 — same class as the other fetchers; ports surface via `run_health_check`), plus `is_server_active` / `list_instances` (both collapsed into `get_status`).

**Count: 12 model-facing (locked 2026-06-12).** 10 core (`get_status`, `get_audit_log`, `run_health_check`, `trace_root_cause`, `get_change_timeline`, `list_blueprints`, `server_command`, `install_server`, `uninstall_server`, `open_ports`) + the config pair (`view_config_file`, `edit_config_file`). Down from 14: status merged into `get_status`, `get_network` moved internal (Q2).

---

## 5 · Result envelope (architecture.html §5·c)

```ts
type Confidence = "confirmed" | "likely" | "possible";
type Severity   = "info" | "success" | "warn" | "danger" | "update";
type CheckState = "pass" | "warn" | "fail" | "skip";
type Verb       = "start" | "stop" | "restart" | "update" | "backup" | "uninstall" | "edit_config" | "open_ports"; // install is a *create* command (no {id}) — NOT a Verb; see §4·Q3. edit_config = edit_config_file (§3.8)
interface Ref { resource: "server"|"host"|"audit"|"metrics"; id: string; section?: string; }

interface ToolResult<K, D> {
  tool: K;                  // producing tool = website card kind
  confidence: Confidence;   // drives the trust badge
  subject: Ref;             // what it's about
  links?: Ref[];            // surface builds the route / URL
  summary: string;          // ALSO the model's grounding text (not just a fallback)
  data: D;                  // varies by tool/card kind; surface-only
}
```

The model sees only each tool's **name, description, parameters** — never the result shape. The backend fills `data` from authoritative sources. Adopt this envelope as the dispatcher's return contract now, even ahead of the website, so Discord and web share it.

---

## 6 · Consumer-agnostic = enforced shared vocabulary
"Data-only" is not "raw JSON per tool." Agnosticism only holds if there's a central, versioned envelope + fixed taxonomy (the types above). Otherwise tools drift and the two surfaces diverge. The toolbox owns and enforces the schema centrally.

---

## 7 · Open questions
1. ~~**Synthesis strategy for `trace_root_cause`**~~ — **resolved (2026-06-11): deterministic only** — timeline + rules table; the scoped-LLM fallback is retired (§3.4 / §3.0).
2. ~~**Which primitives are model-facing vs internal-only**~~ — **resolved (2026-06-12): `get_network` joins `get_performance`/`get_console`/`get_config`/`get_host_diagnostics` as internal-only** (ports surface via `run_health_check`). Model-facing reads = the coarse/event ones (`get_status`, `get_audit_log`, `get_change_timeline`, `list_blueprints`) + the aggregators; every fine subsystem-fetcher is internal. (§4.1)
3. ~~**Catalog superset vs website-scoped**~~ — **resolved (2026-06-11): shared *capability* superset, uniform catalog, realized by coarsening** (§4 scope note). `install`/`uninstall` kept model-facing (two asymmetric tools, different confirm tiers); `is_server_active`/`list_instances` internal-only; `backup` folds into `server_command`; surface-scoping rejected. Catalog **= 12 model-facing** (§4.1; status merged into `get_status`, `get_network` internal per Q2, + config pair §3.8; `install` is a create command, not a `{id}` verb).
4. ~~**Where new data lives**~~ — **resolving (2026-06-11):** the monitor is now a real **spin-off host-monitoring project** that embeds the (now Native-AOT/trim-safe) `kgsm-lib`. Per-instance metrics (CPU/mem/disk/processes) live there, sampled from **real** sources (`/proc`, cgroups, `DriveInfo`/`statvfs` — never fabricated, per §9); `kgsm-lib` is its **inventory source** (which instances exist + each one's PID/dirs/ports/systemd-unit/compose-file) via the two-call recipe documented in `kgsm-lib/docs/host-monitoring-inventory.md` (`GetAll()` static footprint + `GetAllStatuses(fast)` runtime — never re-shelling kgsm). Host snapshots still come from KGSM `system info`; per-tool placement otherwise per the §8 audit.
5. **Discord rich-confirm affordance (Q5)** — **V1 resolved (2026-06-12): a plain Confirm/Cancel button for all commands**, including `uninstall` and `edit_config_file`. The richer affordances — `uninstall` **type-the-name**, `edit_config_file` **diff review** — are **deferred post-V1, not dropped**. *Residual V1 risk (accepted by owner):* `uninstall` is destructive yet one Confirm-tap away — still human-gated (the proposal names the target), just lower-friction than the eventual type-the-name. (Separate build-spec, **not** an open question: `install` needs a create/provision command shape with no `{id}` — §4·Q3 / §5.)

---

## 8 · Capability audit — KGSM (`~/kgsm/kgsm.sh`, v3.0.0-rc1 / 2.2.0-117)

Audited the up-to-date tree at `~/kgsm` (not the stale global `/usr/local/bin/kgsm`). The read-only `--json` commands were **run, not just inferred** (✓ rows below are confirmed against live output from the `7dtd` instance).

Command surface: `install/uninstall`, `instances` (create/remove/list/info/status/find/…), `lifecycle` (start/stop/restart/status/is-active/logs), `network` (ports check/test-port/test-all/list-used/conflicts/ip/dns), `system` (info/uptime/load/memory/disk/reboot-required), `events` (emit/socket/webhook), `files` (management/systemd/ufw/symlink/upnp), `config` (KGSM's own config), `watcher` (logs/ports), `blueprints`.

### Existing components change the "third party" answer — and contradict the doc
The backend is **not** greenfield. Three .NET components already exist:
- **`~/kgsm-lib` (KGSM.Lib)** — complete C# wrapper over KGSM: `InstanceService`, `LifecycleService`, `SystemService`, **`NetworkService`**, **`ConfigService`**, `FileService`, `BlueprintService`, `EventService` + `UnixSocketClient` + `LogSubscriptionService`, `Utilities/LogParser`. **This is the capability layer** — it already returns structured data for commands whose CLI lacks `--json` (network/config), so the toolbox sits on KGSM.Lib, never shells out to `kgsm.sh`.
- **`~/kgsm-api`** — a .NET 9 Web API over KGSM with host-metrics + SignalR log-streaming scaffolding. **Most of its metrics are fabricated** (see §9): only **disk** (`DriveInfo`) and **host network traffic** (`NetworkInterface` stats) are real; CPU and memory are invented. Being scrapped (§9) — listed here only because its *log-streaming pattern* is worth harvesting.
- **`~/kgsm-watchdog`** — empty README; intent unknown (candidate monitor — check later).

> **Decision required (contradicts architecture.html's "yet-to-be-made backend API").** `kgsm-api` exists and already covers host metrics + log streaming. Before building anything we must decide: **extend `kgsm-api` into the assistant backend, or build greenfield and reuse `kgsm-lib`?** This changes the size of the third-party column. Not yours to assume — raise with the team.

**Legend:** ✓ confirmed available today · ~ partly available / needs a small KGSM or lib addition · ✗ genuinely new build (monitor / persistence / aggregator).

| Tool | Verdict | Detail (confirmed against live output where ✓) |
|---|---|---|
| `get_server_status` | ✓ except players | `lifecycle status --json` returns `status`, `process.pid`, `version.{current,latest,checked,updates_available}` (now **tri-state / honest** — a fast read reports `checked:false` + `updates_available:null` + `latest:null` instead of the old fabricated `false`; shipped 2026-06-11, see §11.2a), per-instance `resources.disk_usage`, `recent_logs`. `ip` = host `external_ip` (`system info`) + instance `ports` (`instances info`). **Only `players.current/max` is missing** — game-protocol query (✗). |
| `get_audit_log` | ✗ (enrich done) + **KGSM gap** | KGSM emits a typed event taxonomy over the socket. **(a) Enrich — DONE 2026-06-14:** events now carry a top-level `Actor` (from `$KGSM_EVENT_ACTOR`, else the invoking OS user — the only stateless-correct source) alongside the existing `Timestamp`; kgsm-lib 1.6.0 surfaces both on `EventWrapper` + `EventDataBase` (copied onto each dispatched event, no handler-signature change). **(b) Persist — still ✗:** `EventService` is **relay-only**, so a downstream consumer must persist + serve windowed queries — **never KGSM** (stateless engine; owner decision 2026-06-14). *Caller wiring still pending:* until the bot/assistant/watchdog set `$KGSM_EVENT_ACTOR`, every event's actor defaults to the OS user. |
| `get_performance` | ✗ | Almost entirely new build. `kgsm-api`'s host CPU/mem are **fabricated** (§9) — don't reuse. Real host signal is limited to disk + net-traffic counters; **per-instance** cpu/ram/net time-series and game **tick** don't exist anywhere — monitor work. |
| `get_console` | ✓ (raw) + ✗ flagging | `lifecycle logs`; `lifecycle status` even carries `recent_logs`. `kgsm-lib` has `LogParser` + `LogSubscriptionService` (live). Severity flagging is our layer. |
| `get_config` | ✓ via lib | `kgsm-lib ConfigService` + instance `.config.ini` / game config via `FileService`. Raw read + parse (like `get_console`), not a KGSM gap. |
| `get_host_diagnostics` | ✓ + ~ | `system info --json` confirmed: `disk`, `memory` (total/used/free/available — **no swap**, one `free` line away), `load`, `uptime`, `reboot_required`, host `external_ip`. Missing **neighbours**/**zombies** — derive in backend from `instances list` + `ps`, or small KGSM add. |
| `get_network` | ✓ via lib + ✗ traffic | Ports required-vs-reachable via `network` (`kgsm-lib NetworkService` returns it structured despite the **CLI having no `--json`**). Per-interface **traffic** counters live in `kgsm-api` (host) — per-instance traffic is monitor work. |
| `run_health_check` | ✓ **SHIPPED 2026-06-14** (was ✗) | First aggregator. V1 checks = liveness + log-errors + update-available + host disk (ports/config-sanity → V1.1). Pure `HealthCheckAggregator` + the `ToolResult<K,D>` envelope; live on the Service + Discord (narrates). See the §3.4 SHIPPED note. |
| `trace_root_cause` | ✗ (aggregator+inference) | Backend; composes logs + audit + network + performance. Depends on audit-log + performance first. |
| `get_change_timeline` | ✗ | Same source as audit-log — needs the persisted, actor+timestamp-enriched event history. |
| `server_command` start/stop/restart | ✓ | `lifecycle` / `kgsm-lib LifecycleService`. |
| `server_command` **update** | ~ **KGSM gap** | Update *detection* exists (`version.updates_available` in a **non-fast** `lifecycle status`; a fast read now honestly reports `null`/unchecked rather than a guessed `false` — §11.2a), but **execution is interactive-menu-only — no headless `kgsm update <instance>`.** KGSM should surface it as a first-class CLI/lib call. |
| `open_ports` | ✓ | `files ufw` + `files upnp`, gated by `enable_firewall_management` / `enable_port_forwarding`. |

### Cross-cutting findings
- **Status `version` is now honest (shipped 2026-06-11).** `get_server_status`'s update-availability used to be quietly wrong on the cheap path: a `--fast` read fabricated `updates_available:false`. Fixed at the template root — fast/unknown-version reads now emit `checked:false` + `updates_available:null` + `latest:null`; only a checked read asserts a boolean. The field is tri-state → C# `bool?` (the lib model was also fixed; a latent `updated_available` typo meant it had *never* deserialized). Net for the audit: this row's "update-availability" is now either a verified value or an explicit "unknown" — never a silent guess. Detail + live verification in §11.2a.
- **The capability layer already exists (`kgsm-lib`)** — uneven CLI `--json` (`network`/`files`/`watcher` lack it) doesn't block us, because the lib already parses those into structured data. Adding `--json` to the CLI is nice-to-have, not a prerequisite.
- **Events are the foundational gap, and it's two-part:** ~~KGSM must **enrich** events (actor + timestamp)~~ **(done 2026-06-14 — `Actor` from `$KGSM_EVENT_ACTOR`/OS-user + the existing `Timestamp`; kgsm-lib 1.6.0 surfaces them)**, then a downstream consumer must **persist + query** them (**not** KGSM — the engine stays stateless; persistence lives in the consumer). This unblocks `get_audit_log`, `get_change_timeline`, and the correlation in `trace_root_cause`. Nothing today persists events; the actor is OS-user until callers set `$KGSM_EVENT_ACTOR`.
- **`update` is the only real command-tool gap** — detection exists, headless execution doesn't.
- **The monitor data plane** (per-instance cpu/ram/net time-series, game tick, player counts) is the main genuinely-new work — built from scratch (`kgsm-api`'s host metrics are mostly fabricated, §9; only disk + net-traffic are real signal).

## 9 · `kgsm-api` viability verdict — **start fresh** (harvest one component)

Audited `~/kgsm-api` (single commit "WIP: Initial commit", 2025-10-02, ~8 months stale, **2850 LOC, zero tests**, .NET 9, references `kgsm-lib`). Verdict: **do not build on it — start fresh.** It's a narrow proof-of-concept whose central component is actively harmful.

**Disqualifying findings:**
- **`SystemMetricsService` fabricates metrics** — per-core CPU is `Random.Shared` noise around a smoothed average (`// Simulate per-core variation`); "used memory" sums every process's `WorkingSet64`; "total memory" is the **GC heap limit** (`GC.GetGCMemoryInfo`), not system RAM; CPU fallback returns a random number. This **violates the architecture's #1 rule** ("never invent a metric") and is *worse* than KGSM's own `system info --json`, which reads `free`/load correctly. Unsalvageable.
- **Synchronous command model** — `Start/Stop/Install` block on the shell-out and return stdout (200/500). The doc + assistant need **202 + `job` + WS tracking + `command.verified`**. No job concept exists.
- **API leaks `kgsm-lib` models** — returns `Dictionary<string, Instance>` (the lib's domain type) as the contract, not the doc's designed server-summary DTO (status/players/cpu/ram/…).
- **Transport = SignalR** (two hubs) — diverges from the doc's raw-WS `{topic,type,data}` + SSE, and from the assistant service's existing SSE.
- **No auth** (`UseAuthorization()` with a "if needed in future" comment), **versionless routes** (`/api/kgsm/[controller]`, not `/api/v1`), and **~25% scope** (Instances/Blueprints/host-metrics/log-streaming; missing jobs, alerts, audit, players, backups, files, config, library, account, and the assistant SSE).

**Worth harvesting (small, mostly as reference):**
- **`LogStreamingService` design** — genuinely decent: multiplexes one `kgsm-lib` log subscription to N clients with a 1000-line ring buffer, history replay, and inactive-stream cleanup. Reuse the *pattern* (swap SignalR → SSE/WS). It also confirms **`kgsm-lib` emits structured, leveled log entries** (timestamp/level/message/source) — which answers `get_console`'s "flagged lines": the severity is already there from the lib's `LogParser`.
- Clean controller error-handling style and the metric **DTO shapes** (history points) as data-shape inspiration only — not the collection code.

**Why fresh wins:** building on it means inheriting fabricated metrics, the wrong command model, leaked DTOs, the wrong transport, no auth, and no tests — then rewriting ~75% anyway. Greenfield loses only ~1 day of ASP.NET scaffolding (csproj/Program.cs/Swagger/health/`kgsm-lib` ref), cheap to recreate clean.

> **Seed for the greenfield backend:** the assistant already lives in `TheKrystalShip.Kgsm.Assistant.Service`, which **has Discord OAuth + a working SSE `/turn`** (verified: `Streaming/SseTurnWriter.cs`, `Contracts.cs`) — far closer to the target architecture (§5 SSE, §6 auth) than `kgsm-api` is. The new backend likely grows from *there* (assistant service + KGSM domain controllers on `kgsm-lib`), not from `kgsm-api`.
>
> **SSE vocabulary now canonical (shipped 2026-06-13).** The service's `/turn` SSE was migrated from its ad-hoc names (`token`/`status`/`confirmation`) to the canonical §5a typed events: **`text.delta` / `tool.start` / `tool.result` / `command.proposed` / `done` / `error`** (`token`→`text.delta`, `confirmation`→`command.proposed`, the coarse `status` replaced by per-tool `tool.start`/`tool.result`). Per-tool events required surfacing tool calls through the generic agent loop (`TheKrystalShip.Llm` `AgentEvent` gained `ToolStart`/`ToolResult`; `LlmAgent.RunStreamAsync` emits all `tool.start` in input order before dispatch, then all `tool.result`). Live-verified end-to-end (gated `Service.Tests/GetStatusLiveTests.cs` → `FleetPrompt_StreamsCanonicalTypedEvents`): both local models stream `tool.start`(get_status, no instance_name) → `tool.result` → `text.delta`×N → terminal `Final`. **Strategic payoff for O1:** the assistant now emits the canonical vocabulary on its own stream, so the future web API can **proxy/relay it verbatim** — that half of O1 is *enabled* (not yet decided; co-deploy + OAuth-sharing still settle at web-API bring-up). **Deferred:** `tool.result` carries the minimal envelope `{tool, summary}` (summary = the dispatcher's string output); the full `ToolResult<K,D>` `data` card (§5) waits for a real surface consumer. `command.verified` is **not** a turn-stream event — it belongs to the command-execution flow (§5·d / Phase-1b command path).

## 10 · Next steps
1. ~~Capability audit~~ — done (§8). ~~Resolve `kgsm-api` vs greenfield~~ — done (§9): **greenfield, seeded from the assistant service.**
2. **KGSM asks** (file upstream): (a) ~~enrich events with **actor + timestamp**~~ **DONE 2026-06-14** (`Actor` from `$KGSM_EVENT_ACTOR`/OS-user + existing `Timestamp`; kgsm-lib 1.6.0 surfaces both), (b) headless **`update`** command, (c) **bulk status** — one call returning all instances' status (already a chosen kgsm fix; see reliability note below), (d) optional `--json` on `network`/`files`, (e) optional neighbours/zombies in `system`.

> **Reliability ceiling — already proven empirically.** The local model does **one `is_server_active` tool round per instance**; with `LlmAgent.MaxIterations=8`, "which servers are running?" *fails* on a box with >7 instances (it gives up with the iteration-cap apology). This is concrete evidence for §3.2's relevance-filter argument and for §3.4's "expose coarse tools, not fine primitives." Two implications for the toolbox: (1) the catalog needs a **bulk/fleet read tool** (`list_running` / `get_all_statuses`) mapping to KGSM ask (2c) — the website's 12 are all single-server; (2) it reinforces that aggregators + bulk reads are how we keep the per-turn tool-round count low enough for the local model.
3. **Event-persistence layer** (KGSM.Lib socket → store → windowed query) — ~~blocked on (2a)~~ **(2a) now done**; the store+query layer lives in a downstream consumer (the engine stays stateless), not KGSM. Unblocks 3 tools.
4. Stand up the toolbox: capability layer on `kgsm-lib`, both filter axes (auth + relevance seam), dual-consumer envelope.
5. Build tools in data-readiness order; the ✓/✓-via-lib tools ship first, monitor-dependent ones (`get_performance` per-instance, traffic, players, `trace_root_cause`) defer behind the monitor + event store.

> **First workstream = KGSM-side bulk reads + `--json`. Detailed design in §11.**

## 11 · KGSM-side workstream: bulk reads + `--json` (measured)

The first concrete work. Scope is deliberately KGSM-only (bash); the one C# method that consumes it is a note, not part of this stream. **Scope is bulk _reads_ only** (status + liveness) — bulk **mutations** (start/stop/restart a set) are explicitly **deferred** to the backend's propose→confirm command-lifecycle stream (decided 2026-06-11): mutations are resource-sensitive (fanning out N starts spikes the CPU/RAM the box reserves for game servers) and security-gated, so they belong with the confirm pipeline, not here.

### 11.0 · Architectural constraint (acknowledged)
KGSM is **stateless with multiple entrypoints** — every invocation (`kgsm.sh`, or a command module directly) re-bootstraps and re-derives its internal state, then exits. There is **no warm process to amortise that bootstrap.** This is load-bearing for the design: the *only* lever to cut the per-call tax is **more work per invocation** — i.e. a bulk entrypoint that bootstraps once and fans out internally. A long-lived status daemon was considered and **rejected** (it would violate the statelessness invariant). The bulk command introduces **no shared/persistent state**: it is one stateless entrypoint that fans out to stateless leaves (per-instance management scripts, each re-deriving its own config). Statelessness is preserved.

### 11.1 · Measured cost model (live box, instance `7dtd`, warm cache)
| Path | Time | What it tells us |
|---|---|---|
| `instances status <i> --json` (no `--fast`) | **~3,300 ms** | full per-instance status |
| `instances status <i> --json --fast` | **~166 ms** | same, update-check skipped |
| `instances list --status --json` (current bulk) | **~3,372 ms** | current bulk runs in the **slow** mode |
| `instances list` (enumerate only) | ~64 ms | glob + names |
| `kgsm.sh -v` (one process) | ~44 ms | bootstrap floor |
| `systemctl is-active u1 u2 u3` (one call) | **~4 ms** | batched liveness **of the systemd subset only** (see 11.1a) |

**Headline:** the inline **update-check is ~95% of a status call** (3,300 → 166 ms, a **20×** swing). Bootstrap floor is only ~44 ms; the per-instance management-script spawn is ~120 ms.

> **Correction (measured 2026-06-11, supersedes the original PR-#1 framing):**
> 1. **The fast bulk read already exists and works.** `instances list --status --json --fast` = **175 ms** (vs 3,573 ms without). `--fast` is globally extracted into `fast_mode` *before* dispatch (`instances.sh:988`) and `_get_instance_status_json` honours it (`:506`). So my earlier "`_cmd_list` never plumbs `--fast`" was **wrong** — a consumer (the future C# `GetAllStatuses()`) gets the fast fleet read today just by passing `--fast`. No bash change needed for that.
> 2. **`--fast` does not *skip* the update-check — it *fabricates* the answer.** `11-status.sh:43-46`: fast mode sets `latest_version="$current_version"` and `updates_available="false"`. Live capture confirmed `"updates_available": false` with no check done. That's a positive "no updates" claim KGSM never verified — the **"never invent a metric"** violation we scrapped kgsm-api for. **This blocks a fast-by-default flip** (it would assert "up to date" for the whole fleet blind) and is the *real* KGSM-side bug.
> 3. **Probe-removal de-scoped.** `_check_management_file_status_support` is the backwards-compat fallback for pre-rc1 management files — untestable here (every instance is rc1) and worth only ~15 ms/instance. Gutting an untestable safety check fails the "don't break anything" bar.
>
> **The honest fix (the real correctness work):** in fast mode emit `version.updates_available: null` (+ a `version.checked: false` flag), not `false` — so *every* existing `--fast` reader becomes honest, not just the bulk path. Catch: the fabrication lives in the per-instance management script generated from `11-status.sh`, so the root fix is a **template edit + `files --instance <n> --create --manage` regen** (the §11.3 regen caveat), and `updates_available` becomes tri-state → C# `bool?`. A no-regen alternative masks it in the bulk-command layer with a `jq` transform when fast (contained, but only fixes the bulk path; single `status --fast` still lies).

→ **`--fast` is the latency win but must be made *honest* first; the default stays non-fast until then. Parallelism remains a distant, bounded second** (and contends with the CPU/RAM the box reserves for game servers).

### 11.1a · Routing invariant (verified — corrects an earlier draft)
KGSM's prime principle: **a created instance is standalone and depends only on its own management script — or, if it has systemd integration, on its systemd unit.** KGSM never assumes which; the `lifecycle` layer **detects per-instance and routes.** Verified in `commands/handlers/lifecycle.sh`:
- `__logic_instance_is_active` (`:268`) reads `lifecycle_manager` from the instance config (cheap `grep`/`cut` via `__get_config_value`, no spawn), then `case` (`:295`): **systemd** → `__logic_is_active_systemd_instance` (`:315`, `systemctl is-active <unit>`, no management script); **standalone** → `__logic_is_active_standalone_instance` (`:332`, `"$management_file" is-active`).
- `__logic_instance_status` (`:360`) does **not** branch — it **always** calls `"$management_file" status`.

**Consequence for the design:** any bulk read **must go through this detection per instance.** A blanket `systemctl is-active`-the-fleet is **wrong** — it would misreport every standalone instance (no unit exists) and usurp KGSM's routing. The bulk command's job is to bootstrap once and *loop the canonical routing*, not to reinvent it.

### 11.2 · Three tiers of bulk read (don't conflate them)
1. **Liveness** — "which are running?" The actual MaxIterations=8 fix. **Routes through `__logic_instance_is_active` per instance** (per 11.1a) — it does **not** bypass the management script for standalone instances. The win is purely **bootstrap-once-then-loop**: a single entrypoint reads each config (cheap) and runs the canonical is-active for each, instead of N full `kgsm.sh` invocations. *Legitimate optimisation within the routing:* partition by `lifecycle_manager` — the **systemd subset** can be collapsed into one batched `systemctl is-active u1 u2 …` (~4 ms; this is the *same* systemd route KGSM already owns, just batched — not a new dependency, and applied only to instances KGSM classified as systemd); the **standalone subset** runs each `"$management_file" is-active` (bounded-parallelisable, 11.4). Natural home is the **`lifecycle`** module (it owns is-active + the detection), reusing `handlers/lifecycle.sh` rather than re-implementing routing in `instances.sh`.
2. **Fast status** — full status blob (pid, version-current, disk, ports, blueprint, recent logs) **minus** update-availability. Always routes through `"$management_file" status` (per 11.1a, faithful). `--fast` semantics, parallelisable, ~166 ms/instance serial.
3. **Update-availability** — the expensive ~3.1 s/instance network check. Keep it **separate and opt-in** (it *is* valuable data the website wants); never block a fleet read on it. **Ownership split that respects statelessness:** KGSM exposes only a *stateless* check (per-instance, plus a batched `--check-updates` form that loops once); **the backend owns cadence + caching** — a long-lived process polls update-availability across the fleet on a schedule and serves the last-known result instantly. A KGSM-side TTL cache is **rejected** for the same reason as the status daemon: it reintroduces persistent state into a stateless tool. The status read returns the cached/last-known `updates_available` the backend already holds; "check now" is a separate action that triggers the live check. Maps to a separate "check for updates" tool, not the fleet status read.

### 11.2a · Output contract (JSON shape) — locked
Keep the **map-keyed-by-instance-name** shape the existing `instances list --status --json` already emits, so `kgsm-lib` maps it straight to `Dictionary<string, T>` (the same convention `GetAll()` → `instances list --detailed --json` already relies on). No new convention.
- **Liveness** (`lifecycle list-active --json` or `--brief`): `{ "<name>": { "active": true, "lifecycle_manager": "systemd" }, … }` — minimal, no version/disk/logs.
- **Fast status** (`instances list --status --json [--fast]`): the existing per-instance status object (status/process/version/configuration/resources/backups/recent_logs). **`version` is now honest (implemented — see "PR #1 shipped" below):** in fast mode `checked: false`, `updates_available: null`, `latest: null` (KGSM did not check); non-fast `checked: true`, `updates_available: <bool>`, `latest: <version>`. The backend may overlay its own last-known `updates_available` onto a fast read; KGSM itself never fabricates one.
- Per-instance failure is an **object, not an abort**: reuse the existing `{ "error": …, "instance": …, "requires_regeneration": true }` element shape so one bad instance never sinks the fleet read. *(Forward-ref: this is the **KGSM wire** shape and is **unchanged**; the **C# lib** model wraps this element into `Reading<InstanceRuntimeStatus>` per §3.7 — only the lib's ad-hoc `Error`/`RequiresRegeneration` pair is retired, not the JSON.)*

> **PR #1 shipped (2026-06-11) — `--fast` honesty fix, not the default-flip.** Measuring killed the original premise: the fast bulk read already works (`instances list --status --json --fast`, 173 ms vs 3.5 s) — `--fast` is globally parsed before dispatch and the status helper honours it. The real bug was that fast mode **fabricated** `updates_available: false`/`latest=current` instead of reporting "unchecked." Fixed at the **template** root (`templates/manage.{native,container}.d/11-status.sh`): added an `updates_checked` flag; fast mode (and the version-unknown case) now emit `updates_available: null` + `checked: false`; the `jq` `version` object is tri-state. Human output: "Not checked (fast mode)". **Verified live on `7dtd`** (native): fast → `{current, latest:null, checked:false, updates_available:null}`; non-fast → `{current, latest, checked:true, updates_available:false}`; bulk carries it; JSON valid; 173 ms held. **No regressions** — full unit suite identical before/after (the 2 reds, `test_instances_logic` 100/1 and `test_files_upnp_commands` 37/3, are **pre-existing**, confirmed via `git stash`); no new shellcheck warnings (fragment SC2148/SC2154 noise is pre-existing). The container template was edited **symmetrically** and syntax-checked (`bash -n`) but **not exercised live** (no container instance on this box). **De-scoped from PR #1:** the default-flip (would assert "up to date" blind) and the probe removal (untestable pre-rc1 fallback, ~15 ms).
>
> **Follow-ups — all fixed (2026-06-11):**
> - (i) **C# model** (`kgsm-lib/Core/Models/InstanceStatus.cs`): `VersionInfo.UpdatesAvailable` → `bool?`, `Latest` → `string?`, added `Checked` (`[JsonPropertyName("checked")]`). **Also fixed a latent pre-existing bug**: the attribute was misspelled `updated_available` (KGSM emits `updates_available`), so `UpdatesAvailable` had **never** deserialized — it silently read `false` always. Build clean (0 warn/0 err); no new test failures (the **20 reds in `InstanceServiceTests` are pre-existing** — confirmed via `git stash`, and flagged in kgsm-lib's own CLAUDE.md as a known blocker).
> - (ii) **Regen:** only `7dtd` exists; regenerated (and again after the header doc-fix). Done.
> - (iii) **Stale docs:** corrected `templates/CLAUDE.md`, `docs/templates.md` (×3), and the **generated-script header** `templates/manage.{native,container}.d/00-header.sh` (it bakes the regen command into every instance) → `files management create <n>`. Verified the recommended `commands/files.sh management create <n>` form works. `CHANGELOG.md`'s older `-i [instance]` form left as a historical record.

### 11.3 · Changes, by file
- **Liveness tier → `commands/lifecycle.sh` + `commands/handlers/lifecycle.sh`** (owns is-active + the systemd/standalone routing).
  - Add a bulk entrypoint (e.g. `lifecycle list-active` / `lifecycle status --all --brief`) that bootstraps once, enumerates via `__logic_get_instances`, and **loops the canonical `__logic_instance_is_active`** per instance (never a blanket systemctl — per 11.1a).
  - Optional refinement: a partitioned helper that reads each `lifecycle_manager`, batches the **systemd** subset into one `systemctl is-active u1 u2 …`, and runs `"$management_file" is-active` for the **standalone** subset. Keep it in the handler so the routing stays in one place and stays testable.
- **Fast-status tier → `commands/instances.sh`** (status already routes through the management script, faithful).
  - `_cmd_list`: **accept `--fast`** (currently dropped) and **default the `--status` path to fast** (update-availability moves to its own flag).
  - `_get_instance_status` / `_get_instance_status_json`: **drop the redundant `_check_management_file_status_support` probe** — it spawns the management script a *second* time just to `grep` its `--help` (doubles per-instance process cost). Detect capability once, or assume current management-script format and handle failure inline.
  - `_list_instances_status_json`: the per-instance `jq -n … | jq -s from_entries` fan-out is fine at small N; if parallelising (11.4) assemble fragments then **one** `jq -s`.
- `templates/manage.{native,container}.d/11-status.sh` (`_get_status`): already honours `--fast` correctly (skips `_get_latest_version`, the costly bit) — **no change needed**, just make sure callers pass it. `du -sh` stays the next-largest cost on big installs; consider making disk usage skippable in the brief tier.
- Regeneration caveat: `11-status.sh` lives in per-instance management scripts, so a template change only reaches instances after `files --instance <n> --create --manage`. Prefer fixes at the `commands/` orchestration layer where possible (no regen needed).

### 11.4 · Parallelism — contingent, bounded, second-priority
Only if measured fleet latency demands it after `--fast`. Constraints: **bounded** (`xargs -P <small>` or a `wait -n` semaphore — never unbounded; the box reserves CPU/RAM for game servers), **corruption-safe** (NUL-delimited or per-worker temp files so concurrent JSON fragments don't interleave), **error-aggregating** (one failed instance must not abort the set — emit a per-instance error object, like the existing `requires_regeneration` shape). Note in output if any instance was skipped/failed. Given `--fast` already takes a 10-instance fleet from ~33 s → ~1.7 s, this is a polish lever, not the fix.

### 11.5 · `--json` additions — driven by the tool catalog, not completeness
Today `--json` exists on `instances`/`lifecycle`/`system`/`config`/`blueprints`; **absent on `network`, `files`, `events`, `watcher`, `directories`** (verified by grep). Add it **only where a §4 tool consumes it**:
- **`network`** (`commands/network.sh`, handler `commands/handlers/network.sh`) — **yes**: `ports`, `list-used`, `conflicts`, `check` feed `check_ports` and the `run_health_check` aggregator. Highest-value `--json` target. (Subcommands `_cmd_ports_*`, `_cmd_test_*` currently emit human text only.)
- **`files`, `events`, `watcher`, `directories`** — **defer**: mostly mutating/admin surfaces with no current read-tool consumer. Add per-subcommand if and when a tool needs it; don't add speculatively.

### 11.6 · C# consumer — `GetAllStatuses` SHIPPED (2026-06-11), but the fan-out only *closes* when a tool calls it
`kgsm-lib` had **no bulk path**: `InstanceService.GetInstanceStatus(name)` / `LifecycleService.IsActive(name)` are per-instance, and the assistant looped them → N entrypoints, each 3-processes-deep = **the literal source of both the MaxIterations=8 cap and the latency.** Added one method on `IInstanceService`/`InstanceService`:
- **`GetAllStatuses(bool fast = false)`** → `instances list --status --json[ --fast]` → `Dictionary<string, InstanceRuntimeStatus>` (mirrors `GetAll()`'s `?? []`). One KGSM bootstrap, the whole fleet in a single call.
- **Live-verified on a real 2-instance fleet** (`7dtd` + a fresh, then-uninstalled `factorio-verify`): fast (`checked:false`, nothing fabricated) and non-fast (`checked:true`, real check); the dict returns **both** instances and does **not** collapse. Full suite green bar the 20 pre-existing stale reds (git-baseline unchanged); **6 new tests** — 3 service arg-shape (executor mock) + 3 real-deserialization (mocked `IProcessRunner` + captured live JSON).

**Two honest caveats:**
1. ~~**Capability shipped ≠ failure fixed.**~~ **RESOLVED 2026-06-13 (Phase 1a).** The merged model-facing `get_status` tool (no `instance_name` → fleet) now wires `GetAllStatuses(fast:true)` through a new `IServerOperations.GetFleetStatusAsync` port: one bulk KGSM bootstrap instead of an N-instance `is_server_active` loop. The old per-instance read tools (`list_instances`/`get_server_status`/`is_server_active`) are **dropped from the model-facing catalog** (`is_server_active` stays an internal port cap). The earlier "gated on the greenfield backend" was wrong — the toolbox lives in the assistant service, which is the seed the backend grows from (§9), not a dependency on it. Verified by a dispatcher test asserting the fleet read is a single bulk call, and **live-verified end-to-end 2026-06-13** against a real installed instance (`factorio-test`, stopped) + the dev-checkout kgsm, with the **tool-call arguments captured** to tell the two routes apart. Both local models (`gemma4:12b`, `qwen3.5:9b`): a **fleet-phrased** prompt ("Which of my game servers are currently running?") produced **exactly one `get_status` call with NO `instance_name`** → the bulk `GetFleetStatusAsync` path (the fix), answering "none running, factorio-test stopped" with no iteration-cap apology; a **named** prompt produced `get_status(instance_name=factorio-test)` → the single-server path. (Honest scope: with the per-instance tools removed, a status loop is now *structurally* impossible regardless of fleet size; one instance can't recreate the original >7-instance loop, so this confirms model route-selection + integration, not a bug reproduction. Gated live test: `Service.Tests/GetStatusLiveTests.cs`, run with `KGSM_LIVE_OLLAMA=1`.)
2. **`GetActiveInstances()` deferred to Phase B (routed `lifecycle list-active`).** Deriving liveness from the bulk `status` field would answer the Tier-1 question with Tier-2 data — `status` always comes from `__logic_instance_status` → `"$management_file" status`, *not* the systemd/standalone routing of `__logic_instance_is_active` (11.1a). For standalone they agree, but a systemd instance's status could contradict the routed answer and there's no systemd instance on this box to verify, so liveness stays in Phase B. ("Which are running?" is still answerable *today* via `GetAllStatuses(fast:true).Where(s => s.Status)` at the toolbox layer, with that same management-script-status caveat.)

> **Follow-up (`Reading<T>` unification — §3.7) — SHIPPED 2026-06-13 (Phase 0).** `GetAllStatuses` now returns `Dictionary<string, Reading<InstanceRuntimeStatus>>`; the nullable `Error`/`RequiresRegeneration` pair on `InstanceRuntimeStatus` is retired. A failed element deserializes to `Reading{ state: Unavailable, code: RequiresRegeneration }` via the new `KgsmBulkStatusReadingConverter` (maps KGSM's polymorphic status-or-error element); `Reading<InstanceRuntimeStatus>` + the dict are registered in `KgsmJsonContext` and the converter is AOT-clean (verified: 0 IL2026/IL3050, 429 lib tests green). Landed before any consumer wired the bulk read, as required. `Reading<T>`/`ReadingState`/`ReadingCode` live in `kgsm-lib/Core/Models/Reading.cs`.

**Also fixed (pre-existing latent bug surfaced by the live run):** `InstanceRuntimeStatus.RecentLogs` was typed `IReadOnlyList<string>` but KGSM emits `recent_logs` as a **string** (logs present) *or* the **array `[]`** (no logs) — so the single `Deserialize<Dictionary>` threw on real output and the read came back empty. `RecentLogs` → `string` + a scoped `JsonRecentLogsConverter` (string→passthrough, `[]`→`""`, null→`""`) on that one property. This **also repairs single-instance `GetInstanceStatus`** — which had **zero production callers** (tests only), so it was latent, but would have bitten the first `get_server_status` consumer. Confirmed-from-source error-element keys (`error`/`instance`/`requires_regeneration`) mapped to nullable `Error`/`RequiresRegeneration` so a failed bulk element is **detectable**, not a silent empty "stopped" object.

> **KGSM-side follow-up (separate, NOT done):** the `recent_logs` polymorphism is a real template inconsistency — `templates/manage.{native,container}.d/11-status.sh` emits a JSON *string* (line 96, `jq -R -s .`) for logs-present but the array `[]` (lines 98/101) for no-logs, **and** the human-readable branch (lines 207-208, `jq -r '.[]'`) assumes an *array*, so it breaks for the logs-present string case. The honest root fix (always emit a string; fix the human path) is its own KGSM PR + regen; the C# converter makes the lib robust meanwhile (incl. against not-yet-regenerated instances).
