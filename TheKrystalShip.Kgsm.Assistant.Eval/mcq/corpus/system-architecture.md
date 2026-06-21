# KGSM Ecosystem — System Architecture (the keystone)

**Status:** Living · **Created:** 2026-06-11 · **Level:** C4 context + container

> This is the **map, not the territory.** It owns the system *topology*, the
> *ownership assignments* for every cross-component contract, the *open
> structural decisions*, and a *status* tag on everything — and nothing else.
> It does **not** re-specify any seam's facts: each fact has exactly one owning
> doc (see §6 + §9), and this file points there. If you find a contract detail
> written *here*, that's a bug — move it to its owner and leave a pointer.
>
> Lives untracked in `TheKrystalShip.Llm/` alongside `architecture.html` and
> `assistant-toolbox-plan.md`; not committed (same limbo as those).

## Status legend
`built` = exists and verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided.

## 1 · Component inventory

| Component | Responsibility | Status | Source-of-truth doc |
|---|---|---|---|
| **kgsm** (bash) | Stateless authority: instances/config/blueprints; emits events over named unix sockets. **Native lifecycle (start/stop/is-active/enable) now routes through kgsm-watchdog**; kgsm still owns event emission | `built` | `kgsm/CLAUDE.md`, `kgsm/docs/` |
| **kgsm-watchdog** (C#, Native-AOT) | Resident supervisor — the **stateful half of the engine, peer of kgsm**. Owns `kgsm.slice` + per-instance cgroups, spawns native instances into them, holds desired-state, does crash-restart + autostart. Deliberately breaks the stateless invariant (§4, eyes-open) | `built` (Inc 1–5; deployed on test host, reboot-test pending) | `kgsm-watchdog/PLAN.md`; memory `kgsm-watchdog`, `kgsm-cgroup-supervision` |
| **kgsm-firewall** (C#, Native-AOT) | The **host-firewall authority** — the single isolated/auditable door for host-firewall state (open/close/list per instance) behind a firewall-agnostic driver seam (ufw first; firewalld/nft/iptables later). Socket-activated privileged helper (root, idle-exits between ops), hard-fail install. **Not the watchdog, not a kgsm-lib dependency** — owns its own `PortDto`; kgsm-lib consumes its `Firewall.Contracts`, never the reverse; emits no kgsm events itself (the caller does) | `built` (Inc 0–4 of 4: core + ufw driver, socket-activated daemon + bundled CLI, `Firewall.Contracts` 1.0.0 + kgsm-lib `IFirewallService`, **Inc 3 bash cutover — `files.firewall.sh` (Inc-4 rename of `files.ufw.sh`) routes through the authority via a `handlers/firewall.sh` chokepoint, hard-fail on unreachable, + the `instance_ports_opened`/`_closed` events (kgsm-lib 1.12.0 vocabulary)**, **Inc 4 de-ufw strip + rename — `files ufw`→`files firewall`, dead `ufw.tp`/`firewall_rules_dir` removed, kgsm-lib `CreateUfw`/`RemoveUfw`→`CreateFirewall`/`RemoveFirewall` 1.13.0**; **DEPLOYED + live-validated 2026-06-16** — binary at `/opt/kgsm-firewall`, socket unit enabled (boots), env pins `KGSM_FIREWALL_BACKEND=ufw`; full kgsm→daemon→ufw round-trip + structured `instance_ports_opened`/`_closed` events proven on a factorio instance, then ufw restored to inactive) | `kgsm-firewall/CLAUDE.md` + `PLAN.md`; `headless-network-plan.md §7`; memory `kgsm-firewall` |
| **kgsm-lib** (C#, Native-AOT) | The single C#↔KGSM chokepoint — process-exec + JSON + event socket → typed C#, *no matter what*. Consumed by **every** C# component: monitor, assistant, web API, and the Discord bot. Also the C#↔firewall chokepoint (`IFirewallService`) and C#↔watchdog (`IWatchdogClient`) | `built` | `kgsm-lib/CLAUDE.md` |
| **kgsm-monitor** (C#, Native-AOT) | Consumer-agnostic resource-metrics daemon (htop/btop-style, reads `/proc`/`/sys`/cgroups); self-ticks 1 Hz; serves latest snapshot over a unix socket (HTTP). **Host metrics are standalone (zero deps); per-server is an additive plug-in** — embeds kgsm-lib when a kgsm is reachable, reads the `kgsm.slice/<inst>` cgroups the watchdog owns (proc-tree fallback when absent) | `built` (host + per-server cgroup/proc; cgroup-first for natives via kgsm-lib 1.5.0 `Instance.CgroupPath`, Inc 4; deployed build not yet refreshed) | `kgsm-monitor/PLAN.md` |
| **Assistant service** (`TheKrystalShip.Kgsm.Assistant` + `.Llm`) | Bridges **Ollama** ↔ KGSM (via kgsm-lib) ↔ tools; exposes its **own HTTP API** (turn SSE, tool calls). A shared service consumed by *both* the web API and the Discord bot | `partial` (Discord-OAuth + SSE turn exist) | `assistant-toolbox-plan.md`, memory `kgsm-assistant-extraction` |
| **Control Panel web API** (REST/WS/SSE) | **Aggregator** — joins kgsm-lib (domain + supervision), kgsm-monitor (metrics) and the assistant behind one Discord-OAuth'd surface, in a single pipeline to the SPA. Joins status + metrics; never infers run-state from metric-presence (§4). Built in `kgsm-api` (active rewrite) on **standard JIT — controllers + EF Core, the deliberate exception to the AOT ecosystem** (not embedded; eyes-open maintainability trade, `kgsm-api/PLAN.md §8`) | `partial` (M0–M4·a built — skeleton + hosts + servers join + stream WS + commands + auth; M5 audit log built, frontend gate pending — `smoke.sh` 31/31, `Api.Tests` 30/30) | `kgsm-api/PLAN.md` + `architecture.html` (external surface) |
| **React SPA** (Control Panel) | Dashboard / control center; caches server truth, renders, sends explicit commands. Lives in **`kgsm-web`** — a standard Vite + React 18 (JSX) build ported from the `krystal-design` prototype | `partial` (UI complete against bundled fixtures; not yet wired to the web API — auth/realtime pending) | `kgsm-web/README.md` + `architecture.html` |
| **Discord bot** | Parallel surface: integrates the **assistant** + **kgsm-lib** directly (not through the web API), so actions flow in from Discord | `partial` | `assistant-toolbox-plan.md`, memory `kgsm-assistant-extraction` |
| **Assistant CLI** (`TheKrystalShip.Kgsm.Assistant.Cli`, binary `kgsm-assistant`) | Parallel **terminal** surface onto the assistant — one-shot + REPL — on the same backend as the bot (`AddLocalLlm`+`AddKgsmAssistant`+`AddKgsmAdapters`), minus HTTP/OAuth/SSE. Standalone leaf; self-contained single-file binary; authorized-by-default (`--read-only` opt-down); in-process confirmations; provenance `cli:<user>`. Sits on the shared `…Assistant.Infrastructure` seam (the socket-safe kgsm-lib registration, extracted from the service) | `built` | `kgsm-llm` `…Cli/README.md`; memory `assistant-cli-surface` |
| kgsm-api (repo) | **Rewrite underway** — now the home of the Control Panel web API above (M0–M4·a built, M5 partial; `kgsm-api/CLAUDE.md` + `PLAN.md` authoritative). The superseded .NET 9 attempt (fabricated metrics; **not authoritative**) is parked in `kgsm-api/legacy/`, harvest-only | rewrite `partial` | `kgsm-api/PLAN.md`; `assistant-toolbox-plan.md §9` |
| kgsm-web (repo) | **Now the Control Panel SPA** (the React SPA row above). The stale React/Node admin panel that previously lived here was discarded and replaced in-place by a fresh Vite + React 18 build (`kgsm-web/README.md` + `MIGRATION.md` authoritative) | `partial` | `kgsm-web/README.md` |

## 2 · Topology (status-tagged)

```
  ENGINE / control plane — two peers ........................... [built]
  ┌──────────────────────────────────────────────────────────────┐
  │ kgsm (bash) — stateless authority: config/inventory/blueprints,│
  │   emits events.  native start/stop/is-active/enable ──routes──▶│
  │ kgsm-watchdog (C#, AOT) — resident supervisor: owns            │
  │   kgsm.slice/<inst> cgroups, desired-state, crash-restart,     │
  │   autostart.  Stateful by design (the §4 exception)            │
  └──────────────────────────────────────────────────────────────┘
     ▲  kgsm: exec + JSON · events (kgsm.sock → bot, monitoring.sock → monitor)
     │  watchdog: control socket (UDS)
  kgsm-lib (C#, AOT) — single C#↔engine chokepoint ............. [built]
     │  exec + IWatchdogClient · consumed by EVERY C# component below
     ├───────────────┬───────────────┬──────────────────────────┐
     ▼               ▼               ▼  domain reads/commands     ▼
 kgsm-monitor    assistant svc       │                      Discord bot .. [partial]
 resource:       Ollama↔KGSM,        │                      (assistant +
 host (0-dep) +  own HTTP API        │                       kgsm-lib —
 per-server      [partial]           │                       parallel surface)
 plug-in reads       │               │                           │
 kgsm.slice/<i>      │ HTTP          │ domain + supervision      ▼
 cgroups [built]     │ (turn SSE)    │ (status via façade)    Discord
     │ scrape        │               │
     │ GET /metrics  │               │
     ▼               ▼               ▼
    ┌──────────────────────────────────────────────────┐
    │ Control Panel web API (REST/WS/SSE) — AGGREGATOR   │ .......... [partial]
    │ joins monitor(metrics) + lib(domain+supervision)   │
    │ + assistant; metric-presence ≠ status              │
    │ one Discord-OAuth surface                          │
    └────────────────────────┬─────────────────────────┘
                             │ REST + WS + SSE [partial — built, frontend gate pending]
                            ▼
                        React SPA — dashboard ........ [partial — UI built, not yet wired]
```

The engine is live on the test host today — `kgsm + kgsm-watchdog`, native
lifecycle through `kgsm.slice` cgroups. The monitor is `built` (host + per-server)
but its 1.3.0 build isn't redeployed; the assistant + Discord bot are `partial`.
The **web API aggregator** (`kgsm-api`, `partial`) and the SPA (`kgsm-web`, UI built
against fixtures) both exist but are **not yet wired to each other** — the single
pipeline that joins metrics + domain + supervision + assistant for the dashboard is
still to be connected (auth + realtime).

A second privileged authority, **kgsm-firewall**, sits engine-adjacent (not drawn
above): the host-firewall door, reached **two ways** — by kgsm (bash) shelling its
bundled CLI client, and by C# through kgsm-lib's `IFirewallService` (exactly parallel
to how `IWatchdogClient` reaches the watchdog). It is a privileged authority but
**not part of the engine and not a leaf's dependency** — standing root belongs to a
dedicated, isolated door, not the supervisor of untrusted game procs. Built through
Inc 2 (the contract + the C# chokepoint); the bash cutover (Inc 3) that puts it in the
live install path is pending, so today nothing routes through it yet.

## 3 · Seams (the contracts — pointers, not specs)

| From → To | What crosses | Transport | Status | Owning spec |
|---|---|---|---|---|
| kgsm → kgsm-lib | command results, instance config/status | process exec, JSON stdout | `built` | `kgsm-lib/CLAUDE.md §2–3` |
| kgsm → kgsm-lib (events) | instance lifecycle deltas (`InstanceName`, best-effort). **Watchdog acts → kgsm emits → monitor/bot observe** — native lifecycle is watchdog-driven, but kgsm still fires the event (`LifecycleManager` dropped from the payload in lib 1.3.0) | unix socket (`kgsm.sock` → bot, `monitoring.sock` → monitor) | `built` | `kgsm/docs/events.md`; consumer model `kgsm-monitor/PLAN.md §6` |
| CLI / bash → kgsm-watchdog | native lifecycle dispatch (start/stop/is-active/enable) | HTTP/1.1 over UDS (`curl --unix-socket`) | `built` | `kgsm-watchdog/PLAN.md`; `kgsm` `commands/handlers/watchdog.sh` |
| kgsm-lib ↔ kgsm-watchdog | supervision reads + lifecycle commands (`IWatchdogClient`) | HTTP/1.1 over UDS (`ConnectCallback`) | `built` | `kgsm-watchdog/PLAN.md`; memory `kgsm-watchdog` (Inc 3) |
| kgsm-lib ↔ kgsm-firewall | host-firewall open/close/list + backend (`IFirewallService`) — the C#↔firewall chokepoint, parallel to `IWatchdogClient`; maps `PortMapping`↔`PortDto`, honest `Unknown`, `FirewallException` on unreachable | **NDJSON over UDS** (not HTTP — the authority carries no ASP.NET stack); wire shared as the `TheKrystalShip.KGSM.Firewall.Contracts` package | `built` (Inc 2 — kgsm-lib 1.11.0, `Firewall.Contracts` 1.0.0) | `kgsm-firewall/PLAN.md`; memory `kgsm-firewall`, `kgsm-canonical-port-format` |
| kgsm (bash) → kgsm-firewall | host-firewall open/close/list — bash shells the authority's **bundled CLI client** (`kgsm-firewall ensure-open/remove/list`), never parses the wire; **asymmetric** hard-fail (enable aborts on unreachable, disable warns+continues) | NDJSON over UDS (binary is its own client) | `built` (Inc 3 — `files.firewall.sh` → `handlers/firewall.sh` chokepoint → authority; `EC_FIREWALL_UNREACHABLE`; emits `instance_ports_opened`/`_closed`. Inc 4 renamed the verb `files ufw`→`files firewall` + the module `files.ufw.sh`→`files.firewall.sh`) | `kgsm-firewall/PLAN.md`; `headless-network-plan.md §7h` |
| kgsm-watchdog → kgsm-monitor | **per-instance cgroup path** (`kgsm.slice/<inst>`) — watchdog owns it, monitor reads off the cgroup **filesystem** (no process dependency on the daemon) | filesystem contract (`/sys/fs/cgroup`) | `built` (Inc 4 — kgsm emits `cgroup_path`, `Instance.CgroupPath` in kgsm-lib 1.5.0; monitor reads the cgroup, proc-tree the fallback) | **`kgsm-lib/docs/host-monitoring-inventory.md`** (owns inventory + cgroup-path) |
| kgsm-lib → kgsm-monitor | **per-instance inventory** (which instances, PID/dirs/ports, identity) | in-process (embedded lib) | `built` (Slice 2) | **`kgsm-lib/docs/host-monitoring-inventory.md`** (owns the inventory + instance-identity contract) |
| kgsm-lib → assistant | domain reads + commands (the assistant's KGSM tools) | in-process (embedded lib) | `partial` | `assistant-toolbox-plan.md §8` |
| kgsm-lib → Discord bot | domain reads + commands (actions from Discord) | in-process (embedded lib) | `partial` | memory `kgsm-assistant-extraction` |
| kgsm-lib → assistant CLI | domain reads + commands (terminal surface) | in-process (embedded lib, via the `…Assistant.Infrastructure` `AddKgsmAdapters` seam) | `built` | `kgsm-llm` `…Cli/README.md`, memory `assistant-cli-surface` |
| kgsm-lib → web API | domain reads + commands **+ supervision** (run-state façade + `IWatchdogClient`) — the aggregator's KGSM surface | in-process (embedded lib) | `built` (M0–M4·a wired; the API embeds kgsm-lib and consumes `IEventManagementService`, `ILifecycleService`, `IWatchdogClient`) | `assistant-toolbox-plan.md §8` |
| assistant → Ollama | prompts ↔ completions (tool-calling LLM) | HTTP (local, RTX 3060, must stay in VRAM) | `partial` | memory `discord-llm-bridge`, `opencode-ollama-setup` |
| kgsm-monitor → web API | latest metrics snapshot | scrape `GET /metrics` (unix socket); shape shared as the **`TheKrystalShip.KGSM.Monitor.Contracts`** package (DTO graph + source-gen camelCase JSON) so producer & consumer share one build-time contract — bump the package version on any change | `partial` (M1·a: `kgsm-api` scrapes it → host capacity + §4·b capabilities) | `kgsm-monitor/PLAN.md §9` (owns the shape, ships the package); `kgsm-api/PLAN.md §6` |
| assistant → web API | chat turn: text/tool/command events | HTTP (turn SSE, aggregated/relayed) | `planned` | `architecture.html §5`, `assistant-toolbox-plan.md §5` |
| assistant → Discord bot | chat turn (rendered to Discord) | HTTP (assistant's own API) | `partial` | memory `kgsm-assistant-extraction` |
| web API → SPA | domain state, console, metrics, audit, alerts | REST + WS channels | `planned` | `architecture.html §3,§3·b` |
| web API → SPA (assistant) | the assistant turn, re-exposed | SSE `assistant/turn` | `planned` | `architecture.html §5` |
| surfaces → {web API, bot} | explicit commands (start/stop/confirm/…) | REST POST/PATCH (web) · Discord (bot) | `planned` / `partial` | `architecture.html §1,§5·d` |

## 4 · Invariants & boundaries (system-wide, owned here)

These are *cross-component* principles the whole system must honor; per-component
docs implement them.

- **Server is authoritative; never fabricate.** No component invents a metric,
  status, or alert — measured or "unknown." (This killed `kgsm-api`; it's why
  the monitor reads `/proc` not `top`.) Owner of the principle: this file;
  enforced in `architecture.html §1` and `assistant-toolbox-plan.md §9`.
- **Metric-presence is never a status.** A surface joins *status-from-the-authority*
  (kgsm-lib's run-state façade: native→watchdog, container→Docker) with
  *metrics-from-the-monitor*, and **never infers run-state from whether a metrics
  row exists** — absence means "not measurable right now," not "stopped." This is the
  rule that keeps the monitor↔watchdog overlap from leaking into the aggregator.
  Owner: this file; binds the (planned) `kgsm-api` design.
- **Sampling stays O(1) in client count.** Fan-out lives in the **web API**; the
  monitor samples once and serves latest. (`kgsm-monitor/PLAN.md §2`.)
- **The kgsm *CLI* stays stateless; the watchdog is the contained exception.** No
  component pushes persistent state back into the kgsm CLI; cadence/caching/persistence
  live above it. The **kgsm-watchdog** is the one resident, stateful piece (desired-state
  + warm process) — accepted eyes-open as the engine's runtime half, *not* a leaf.
  (`assistant-toolbox-plan.md §11`; memory `kgsm-cgroup-supervision`.)
- **Process/trust boundaries:** monitor runs **root** (needs `/proc/<pid>/io`,
  all cgroups), exposes an **unauthenticated** socket gated by **FS perms** only —
  it authenticates nothing. The network-facing services (**web API**, **assistant**)
  enforce **Discord-OAuth**. (`kgsm-monitor/PLAN.md §3 D6–D7`, `architecture.html §2`.)
- **Leaves are independently deployable; aggregation is additive.** Every leaf
  (`assistant`, `monitor`, `bot`) runs and is **fully functional standalone**,
  co-located with a `kgsm` on its host (kgsm-lib is **local** — process-exec + local
  sockets — so a leaf manages its *own* host's kgsm, never a remote one; the
  deployable unit is a host = `kgsm` + any subset of leaves + optional API). The
  Control Panel **API aggregates whichever leaves are present and must degrade
  gracefully when any is absent** — a missing leaf removes only its capability, never
  breaks the base. The **watchdog is engine, not leaf** — base on any host that runs
  native instances; so "monitor without watchdog" is *resilience* (host metrics keep
  working, per-server degrades to proc-tree), **not** a first-class topology. The
  monitor's per-server cgroup metrics are an **additive plug-in over the engine**: host
  metrics need zero deps, per-server lights up only when a `kgsm` is reachable (inventory)
  and is *accurate* only when the watchdog has placed instances in `kgsm.slice/<inst>`.
  **No leaf ever depends on the API or on a sibling leaf**; the only
  inward edges are the one-directional spine (§2) and the accepted `kgsm ↔ watchdog`
  coupling. Shared *external config* (e.g. all surfaces authenticating against the
  same Discord app/guild/role for consistent authority) and added credentials are
  **not** dependencies — independence is measured on the inter-service process axis.
  Owner: this file; **binds the (planned) `kgsm-api` design** (`architecture.html`).

## 5 · Open structural decisions

| # | Decision | Notes / where it resolves |
|---|---|---|
| O1 | **Web API ↔ assistant boundary.** Clarified 2026-06-11: the **assistant is its own service** (own HTTP API); the web API **aggregates** it (+ monitor + lib), not hosts it. Remaining open: does the web API *proxy/relay* the assistant's turn SSE verbatim or re-wrap it, and do web API + assistant share one OAuth/deployment? **2026-06-13: SSE-shape half de-risked** — the assistant's `/turn` now emits the canonical §5a typed vocabulary (`text.delta`/`tool.start`/`tool.result`/`command.proposed`/`done`/`error`; `assistant-toolbox-plan.md §9`), so **proxy-verbatim is now *enabled***. Still open: the proxy-vs-rewrap *decision* + co-deploy/OAuth-sharing. | The SPA still sees "one surface" (`architecture.html §2`), presented by the aggregator; the assistant stays a separate aggregated peer. Decide proxy-vs-rewrap + co-deploy at bring-up. |
| O2 | **Metrics relay transport: WS vs SSE.** `architecture.html §3·b` relays metrics over **WS** (`ws hosts/{id}/metrics`); `kgsm-monitor/PLAN.md` (Later) says "authed **SSE**." | Reconcile to one. `architecture.html §3·b` owns backend↔surface realtime; monitor's wording should defer. |
| O3 | **Event persistence** (audit log / change-timeline / root-cause). ~~KGSM events carry no actor/timestamp~~ and nothing persists them. | Two-part: kgsm enriches; backend persists+queries. **Enrich half done 2026-06-14:** kgsm emits a top-level `Actor` (from `$KGSM_EVENT_ACTOR`, else the invoking OS user) alongside the existing `Timestamp`; kgsm-lib 1.6.0 surfaces both on `EventWrapper`/`EventDataBase` (copied onto each dispatched event, no handler-signature change). **Persistence stays downstream of the stateless engine** (a consumer stores+queries — never KGSM). `assistant-toolbox-plan.md §8,§10`. |
| O4 | **kgsm-api** | Scrap confirmed (`assistant-toolbox-plan.md §9`); old code harvest-only in `kgsm-api/legacy/`. **Rewrite now underway** in `kgsm-api/` — per-host aggregator, standard JIT (controllers + EF, the deliberate AOT exception), M0 built; `kgsm-api/PLAN.md`. |
| O5 | **Multi-host identity & sessions** — one Discord login authorizing the SPA against N independently-deployed hosts with no re-login per host. **Resolved 2026-06-13; trust model = owner-run fleet:** *per-host* OAuth authorization-code flow with `prompt=none` silent SSO. SSO anchor = the live `discord.com` browser session, **not** a shared/forwarded token (audience-unbound → replayable across hosts; also barred by the no-token-in-JS posture). Each host reuses its bot's existing Discord *application*, verifies once via `/users/@me` then discards the token, and authorizes via its own bot. | Contract spec → **`architecture.html §6·a`** (per-host bearer in `sessionStorage`; 401/403/`login_required` state machine; renewal + rotation). Central-issuer (Model B) was the only other fork — reserved for an *arbitrary-third-party-host* trust model, not this fleet. |
| O6 | **Monitor ↔ watchdog boundary** (the cross-over: both touch per-instance cgroups). **Resolved 2026-06-14 — control plane vs data plane:** the watchdog *acts* (owns `kgsm.slice/<inst>`, lifecycle, desired-state) and is **engine/base**, a peer of kgsm; the monitor *measures* (reads those cgroups, owner-agnostic — exactly as it reads Docker's) and stays a **standalone leaf** (host metrics zero-dep; per-server an additive plug-in). They share *contracts* — the cgroup path, and the "watchdog acts → kgsm emits → monitor observes" event boundary — **never a process dependency**. Status has two granularities: coarse run-state via kgsm-lib's façade (native→watchdog, container→Docker; `is-active` already routes, Inc-3), supervision detail via `IWatchdogClient`. **Rejected:** folding metrics into the watchdog (collapses act+report = the `kgsm-api` anti-pattern; and the monitor must read host + container cgroups regardless). | Unblock **done 2026-06-14 (Inc 4)**: kgsm derives & emits `cgroup_path`, kgsm-lib 1.5.0 surfaces `Instance.CgroupPath`, the monitor partitions cgroup-vs-proc-tree on whether the cgroup dir exists (no double-count). Cgroup-path contract owner: `kgsm-lib/docs/host-monitoring-inventory.md` (§6 ledger). |

## 6 · Reconciliation ledger (drift found → ownership assigned)

The keystone's job when two docs describe one seam: pick the owner, point the
other at it. It records the **assignment**, not the fact.

| Contract | Symptom | Owner (authority) | Action |
|---|---|---|---|
| Per-instance **identity / inventory** (the overloaded `.pid` = real PID for native / Docker container-id for containers; discriminator is `(LifecycleManager, isContainer=has compose_file)`; containers run `standalone`) | `kgsm-lib/docs/host-monitoring-inventory.md` and `kgsm-monitor/PLAN.md §6` describe it two ways; the lib doc's native/systemd/container framing is less precise | **`kgsm-lib/docs/host-monitoring-inventory.md`** (it's the lib's contract surface the monitor embeds) | Corrected the lib doc to the verified model (this session). **Recommend** the monitor session trim `PLAN.md §6` to *point* at the lib doc rather than restate it. |
| Backend↔surface **realtime transport** | WS (`architecture.html`) vs SSE (`monitor PLAN`) — see O2 | `architecture.html §3·b` | Resolve O2 at backend bring-up; monitor wording defers. |
| Per-instance **cgroup path** (`kgsm.slice/<inst>`, watchdog-owned, monitor-read) | ~~monitor's `ServerCgroupResolver` mapped native→*no cgroup*→proc-tree (its worst path), blind to the `kgsm.slice/<inst>` cgroup~~ — **resolved (Inc 4, 2026-06-14)** | **`kgsm-lib/docs/host-monitoring-inventory.md`** (the lib contract the monitor embeds) | **Done:** kgsm emits `cgroup_path` (derived, not stored), kgsm-lib 1.5.0 `Instance.CgroupPath`, resolver returns it as the native candidate; the proc-tree sampler cedes any native whose cgroup dir is live (shared `FirstExisting` arbiter → no double-count). |
| Per-instance **run-state** (running/stopped/failed) | three potential answers — kgsm `is-active`, watchdog `/status`, monitor metric-presence | **kgsm-lib run-state façade** (native→watchdog, container→Docker; `is-active` already routes, Inc-3) | Coarse status = the façade; supervision detail = `IWatchdogClient`; **metric-presence is never status** (§4). |

## 7 · Build sequence (cross-component, status-aware)

1. ~~kgsm-lib AOT-safe~~ `built` — unblocks monitor Slice 2 (embed) and an AOT backend.
2. ~~kgsm-lib bulk read + monitor inventory contract~~ `built` — `GetAllStatuses`, `Instance.SystemdUnit`, the inventory doc.
3. ~~monitor Slice 2/3~~ `built` — per-server cgroup/proc metrics via embedded lib. ~~Inc 4~~ `built` (2026-06-14) — kgsm emits a derived `cgroup_path`, kgsm-lib 1.5.0 surfaces `Instance.CgroupPath`, the monitor reads the watchdog's `kgsm.slice/<inst>` cgroup for natives and cedes only the no-live-cgroup remainder to the proc-tree fallback (resolver hole closed). Deployed build on the host not yet refreshed.
4. **web API aggregator bring-up** `partial` (M0–M4·a built, M5 partial — `smoke.sh` 31/31, `Api.Tests` 30/30) — the new REST/WS/SSE service in `kgsm-api` (standard JIT, controllers + EF) fronting lib (domain) + monitor (scrape; M1·a hosts wired via the shared contracts package) + the *existing* assistant service (`partial`), behind Discord-OAuth. Staged in `kgsm-api/PLAN.md`; resolves O1, O2.
5. **event persistence** `partial` — kgsm **enrich done** (2026-06-14: `Actor`+`Timestamp` on the wire, kgsm-lib 1.6.0 surfaces them); backend **store/query still `planned`** (downstream of the stateless engine) (O3); unblocks audit/timeline/root-cause tools.
6. **surfaces** `planned`/`partial` — SPA dashboards (via the web API); Discord (parallel, via the assistant + kgsm-lib directly).
7. **headless-network / kgsm-firewall track** (parallel; `headless-network-plan.md`) — UPnP→watchdog `built`; canonical port format `built`; **kgsm-firewall** Inc 0–4 `built` (core+ufw driver, socket-activated daemon, `Firewall.Contracts` + kgsm-lib `IFirewallService`, **Inc 3 bash cutover — routes through the authority, asymmetric hard-fail, + the `instance_ports_opened`/`_closed` events emitted from the bash path; kgsm-lib 1.12.0 carries the C# event vocabulary, emission deferred to the kgsm-api caller**, **Inc 4 de-ufw strip + rename — `files ufw`→`files firewall`, `files.ufw.sh`→`files.firewall.sh`, `EC_UFW`→`EC_FIREWALL`, dead `ufw.tp`/`firewall_rules_dir` removed, kgsm-lib `CreateUfw`/`RemoveUfw`→`CreateFirewall`/`RemoveFirewall` 1.13.0; no behaviour change**). **DEPLOYED + live-validated 2026-06-16** (binary `/opt/kgsm-firewall`, socket unit enabled/boots, env pins `KGSM_FIREWALL_BACKEND=ufw`; full kgsm→daemon→real-ufw round-trip + structured port events proven, ufw then restored inactive). **Feature-complete + deployed.** Feeds kgsm-api M6·b (the `open` verdict via `IFirewallService.ListOwned`). **Phase 2 (symlink privilege elimination) BUILT + COMMITTED 2026-06-16** (kgsm `dev` `980e770`, not pushed): `command_shortcuts_directory` defaults to `$HOME/.local/bin`, the symlink handler never escalates (non-writable → `EC_PERMISSION`), and instance creation warns+skips shortcuts rather than failing — the **last sudo bucket**, so headless KGSM now runs with no per-operation password prompt under the default config (shutdown stays out of scope).

## 8 · Source-of-truth index (where each thing is *really* defined)

- **kgsm** internals → `kgsm/CLAUDE.md`, `kgsm/docs/`
- **kgsm-lib** patterns + AOT/JSON → `kgsm-lib/CLAUDE.md`
- **kgsm-watchdog** supervisor design + increments → `kgsm-watchdog/PLAN.md`; memory `kgsm-watchdog`, `kgsm-cgroup-supervision`
- **kgsm-firewall** host-firewall authority design + increments → `kgsm-firewall/CLAUDE.md` + `PLAN.md`; `headless-network-plan.md §7`; memory `kgsm-firewall`. The C#↔firewall contract — `IFirewallService` (the C# client, in kgsm-lib `Services/FirewallService.cs`) speaking the `Firewall.Contracts` wire package (defined in kgsm-firewall) → described in `kgsm-firewall/PLAN.md` (Inc 2) + memory `kgsm-firewall`
- **kgsm-lib ↔ monitor inventory/identity/cgroup-path contract** → `kgsm-lib/docs/host-monitoring-inventory.md`
- **monitor** design + KGSM-integration facts + slices → `kgsm-monitor/PLAN.md`
- **backend external surface** (REST/WS/SSE, who-owns-what) → `architecture.html`
- **assistant tools + backend-internal decisions + KGSM capability audit** → `assistant-toolbox-plan.md`
- **cross-session project memory** → `~/.claude/.../memory/` (`assistant-toolbox-plan`, `kgsm-assistant-extraction`, `system-metrics-monitor`, …)
