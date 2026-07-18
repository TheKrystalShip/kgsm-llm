# Assistant `/turn` SSE → §5·a compliance spec (kgsm-api M7 prep)

> **Status:** **Phase 1 + Phase 2 IMPLEMENTED 2026-06-19.** Phase 1 = the §5·a envelope
> (WI-1/2/3/4/6/7-default). **Phase 2 (WI-5, the `tool.result` card) is now done** — but via the
> **opaque-payload mechanism**, not the ambient capture this spec originally sketched (see WI-5 +
> §4; `Task.WhenAll` concurrency made order-keyed ambient unreliable). A card is lit only where a
> tool has a **real structured source**: **`run_health_check`** (`HealthData`), **`get_status` fleet
> mode** (`FleetStatusData`), **`search`** (`SearchData` — the cited passages, when the search
> found something to cite; an empty / "couldn't search" result stays summary-only), and
> **`get_performance`** (`PerformanceData` — a live per-server metrics snapshot when the server is
> running with a live frame, OR a windowed history series (`range`+`series`) for a trend read; an empty
> window / not-running / monitor-unavailable read stays summary-only). Single-server
> `get_status`/`read_file` return opaque strings and stay summary-only — fabricating cards for absent
> sources is forbidden (never-fabricate).
> Implementation spec for the upstream half of kgsm-api **M7** (assistant turn relay / keystone
> **O1**); lands in **kgsm-llm** *before* the API relay.
> Authorities: `../../architecture.html §5·a` (the frontend's wire contract — freeze
> from it, don't invent), `../../assistant-toolbox-plan.md §5·a/§5·d` (the canonical
> vocabulary + where `command.verified` originates), `../../kgsm-api/PLAN.md` M7.

## 0 · Why this exists (the decision behind it)

M7 relays the assistant's `POST /turn` SSE to the React SPA. We considered two ways to
reconcile the assistant's stream with the frontend's §5·a contract and **rejected both** in
favour of a third:

- ❌ **Re-wrap in the API** — a translation layer the API owns forever, and it can't honestly
  synthesise fields it doesn't have (a tool-result card, a correlation id).
- ❌ **Proxy verbatim as-is** — the assistant's *current* shapes don't match §5·a's documented
  payloads, so this would force re-opening the frozen frontend contract.
- ✅ **Fix the emission upstream, then proxy near-verbatim.** The §5·a fields are exactly the
  fields the assistant is the honest owner of. Shaping them here makes the API a thin
  passthrough **and** lets every surface (Discord, CLI, future) inherit the richer stream —
  not just the SPA.

### Locked decisions

1. **The API is a near-verbatim relay.** All §5·a shaping happens in kgsm-llm.
2. **Fork (a) — confirmed commands execute through the API's M3 command path**
   (`POST /servers/{id}/commands`), *not* a new SPA→assistant `/confirm` call. One gated +
   audited + verified command path for the whole panel. The assistant's own `/confirm`
   endpoint is **kept** (Discord + CLI use it, and it's the fallback for verbs the API can't
   yet execute — see §6). Consequence: `command.verified` is **not** a turn-stream event
   (it's M3's verify; the assistant's code and toolbox-plan §5·d already agree).

### Blast radius (why this is safe to land upstream)

Only the assistant's **own tests** and the **future SPA** consume the Service `/turn` SSE.
The **bot and CLI use the assistant *library* in-process** (`IServerAssistant` /
`IServerOperations`), never the HTTP SSE — so reshaping the SSE does **not** touch them.
Verified by grep: no `text/event-stream` / `/turn` SSE consumer in `kgsm-bot` or the CLI.

---

## 1 · Target contract (§5·a, verbatim from `architecture.html`)

A turn is one SSE stream (`POST /assistant/turn` → `text/event-stream`). Events:

| Event | Payload | Website renders |
|---|---|---|
| `text.delta` | `{ text }` | streamed answer text |
| `tool.start` | `{ id, tool, label }` | pending "Reading…" pill |
| `tool.result` | `{ id, result }` | resolves the pill + renders its card |
| `command.proposed` | `{ id, verb, subject, confirm, reason?, configKey?, configValue?, instanceName? }` | confirm-first action button |
| `command.verified` | `{ id, ok, headline, lines[] }` | post-action verification block |
| `error` | `{ code, message }` | inline error notice |
| `done` | — | ends the turn |

`architecture.html`'s example frames each event as a flat JSON object carrying a **`type`**
discriminator (`{ "type": "text.delta", "text": "…" }`).

---

## 2 · Current → target (the deltas and their disposition)

| § | Event / field | Assistant emits today | §5·a target | Disposition |
|---|---|---|---|---|
| 2.1 | envelope | SSE `event:` name + `data:{…}` (no in-band `type`) | flat `{ type, …fields }` | **Add `type` in-band, keep the `event:` line** (superset; satisfies either reader) — WI-6 |
| 2.2 | `text.delta` | `{ delta }` (`TokenEvent.Delta`) | `{ text }` | **rename `delta`→`text`** — WI-6 |
| 2.3 | `tool.start` | `{ tool, arguments }` (no id, no label) | `{ id, tool, label }` | **add `id`** (WI-4); `label` **omitted, SPA derives** (WI-7); keep `arguments` (additive) |
| 2.4 | `tool.result` | `{ tool, summary }` (string) | `{ id, result }` (card) | **add `id`** (WI-4); `result` **card staged** (WI-5); `summary` retained until cards land |
| 2.5 | `command.proposed` | `ConfirmationDto { kind, target, instanceName, token, configKey?, configValue? }` | `{ id, verb, subject, confirm, reason? }` | **reshape** (WI-1); **retain `token`** (additive, for `/confirm` surfaces) |
| 2.6 | `command.verified` | not emitted on `/turn` | listed inline in §5·a table | **out of turn scope, AND not emitted by any backend stream** — SPA-composed from the M3 job + verify `server.patch` + the command's `audit.append`; `lines[]` honest-thin (§6) |
| 2.7 | `error` | `{ error }` (one string) | `{ code, message }` | **split** — WI-2 |
| 2.8 | `done` | `{ text, usage }` | — (empty) | **already compatible** (extra fields additive) |
| 2.9 | `thinking.delta` | `{ delta }` | not in §5·a | **keep, opt-in via `think`, document as additive** — WI-3 |

---

## 3 · Work items

### WI-1 · `command.proposed` reshape — **cheap** (Service SSE writer)
**Where:** `TheKrystalShip.Kgsm.Assistant.Service/Streaming/SseTurnWriter.cs` (the
`AssistantEventKind.Confirmation` arm) + `Contracts.cs` (`ConfirmationDto`).

Map the staged `PendingConfirmation` (already in hand) to the §5·a shape:

| §5·a field | Source |
|---|---|
| `id` | synthesised `cmd_<n>` (per-proposal, turn-stable; **not** the token) |
| `verb` | `ConfirmationKinds.Verb(kind)` — *exists already* (`start`/`stop`/`restart`/`update`/`back up`/`uninstall`/`install`/`set config on`). **Normalise to API verb tokens** for routing: `start\|stop\|restart\|update\|install\|uninstall\|backup\|set_config` (see §6 matrix) |
| `subject` | `{ resource, id: Target }` — `resource:"server"` for the instance-targeted kinds (start/stop/restart/update/backup/uninstall/setconfig). `Install` stages a *blueprint* target, not an instance → `resource:"blueprint"` for that kind (the mapping isn't a universal `"server"`). Encode the per-kind resource, don't hardcode `"server"`. |
| `confirm` | human prompt, composed from verb + target (e.g. `"Start factorio-test?"`) |
| `reason?` | optional model rationale (omit for now — no honest source; reserve) |
| `token` | **retained** (additive beyond §5·a) — the host-minted confirmation token for the `/confirm` surfaces (§6) |
| `configKey?`/`configValue?` | **retained** for `set_config` (additive) |
| `instanceName?` | **retained** for `install` (additive) — the optional custom name the user asked for. `subject.id` is the *blueprint* for an install, so the name rides its own field; a surface that installs via an API endpoint passes it through (`POST /servers { name }`) so a named install lands the name instead of dropping it. Null for every other verb and for an unnamed install (kgsm auto-names). |

Honesty: `verb`/`subject`/`confirm` are all derivable from data the assistant holds — no
fabrication. `id` is a display correlation handle, not security-bearing (the `token` is).

### WI-2 · `error` reshape — **cheap** (Service SSE writer + Contracts)
`StreamErrorEvent(string Error)` → `{ type:"error", code, message }`. Introduce a small
closed `code` vocabulary (e.g. `assistant_failed`, `cancelled`, `upstream_unavailable`);
default unknown failures to `assistant_failed` with the real detail in `message`. Never
fabricate a code that overstates what we know.

### WI-3 · `thinking.delta` — **free** (doc only)
Already emitted, already opt-in via `TurnRequest.Think`. Keep it. Reshape its payload key
to `{ text }` for symmetry with `text.delta` (WI-6) and add the in-band `type`. **Document
it in §5·a as an additive, opt-in event** the SPA may render or ignore. No behavioural change.

### WI-4 · tool-call `id` — **modest** (generic LLM loop)
**Where:** `TheKrystalShip.Llm/Models/AgentEvent.cs` + `TheKrystalShip.Llm/Agent/LlmAgent.cs`
(both round paths) → `TheKrystalShip.Kgsm.Assistant` `AssistantStreamEvent` → SSE writer.

Ollama provides **no native tool-call id** (`OllamaStreamParser.ParseToolCalls` reads only
`function.name`/`arguments`), so we **synthesise** one. The loop already emits all
`tool.start` in input order, then all `tool.result` over the *same* `toolCalls` list in the
same index order (`LlmAgent.cs` `RunStreamAsync` ~L217–227 and `ExecuteToolRoundAsync`), so a
per-call id pairs start↔result deterministically:

- Mint `tc_<turnRoundIndex>_<callIndex>` (or a monotonic `tc_<n>` across the turn) at
  `ToolStart`; carry it to the matching `ToolResult` by index.
- Add `string? ToolCallId` to `AgentEvent` and `AssistantStreamEvent` (and their factory
  methods). The generic package owns this — a tool-call id is a generic LLM concept, not a
  KGSM one. **Also a real correctness fix:** today two calls to the *same* tool can't be
  paired by a renderer.

### WI-5 · `tool.result` card (`result`) — **DONE 2026-06-19** (opaque-payload channel)
**Where:** the dispatcher (`IToolDispatcher` impl) + the generic loop's tool-output type +
`AgentEvent`/`AssistantStreamEvent` + SSE writer + `Envelope/ToolResultCard.cs`.

**Mechanism (revised from the original ambient sketch).** The first draft proposed an
ambient per-turn capture (mirroring `IConfirmationContext`) so the generic loop could stay
string-only. That breaks under the loop's **concurrent** dispatch (`DispatchRoundAsync` uses
`Task.WhenAll`): an order-keyed ambient list can't be reliably correlated back to each
`tool.result`, and the dispatcher never sees the synthesised `tc_<n>` id, so any reliable key
would force a generic-layer change *anyway*. So the implemented design is the **opaque-payload
channel** — a small, genuinely domain-agnostic enrichment of the LLM-tool abstraction:

1. `IToolDispatcher.ExecuteAsync` now returns **`ToolOutput(string Summary, object? Data = null)`**
   (`TheKrystalShip.Llm/Models/ToolOutput.cs`) with an implicit `string → ToolOutput` conversion,
   so every summary-only `return "…";` in the dispatcher is unchanged. A tool legitimately has
   two outputs: model-facing text and an optional surface-facing card.
2. The loop carries `Data` opaquely: `ToolExecution` gains `object? Data`, sourced from the
   dispatcher return; `AgentEvent.ToolResult(tool, summary, id, data)` carries it. **`Task.WhenAll`
   preserves input order**, so `outputs[i].Data` is index-aligned to its call, id, and summary
   with **zero keying** — concurrency-proof by construction. The loop **never inspects** `Data`
   and the model is **never** shown it (line 232 still feeds only the string to `LlmMessage.Tool`).
3. The KGSM `ToolDispatcher` builds the card: `RunHealthCheckAsync` returns
   `new ToolOutput(health.Summary, ToolResultCard.From(health))`. `ToolResultCard` (in
   `Envelope/`) is a non-generic projection of `ToolResult<TData>` — `{ tool, confidence,
   subject, data, links }`, the model-facing `Summary` **dropped** (it's already on the frame's
   `summary`; two readers, one result). `Data` is boxed and serialised by runtime type.
4. `ServerAssistant.RunStreamAsync` forwards `ev.ToolData` verbatim (relays, never inspects);
   the SSE writer emits it as `result` (omitted from the frame when null) and adds a
   `JsonStringEnumConverter` so the card's enums render as camelCase strings (`"warn"`/`"pass"`),
   never opaque ints.

> **This overrides the original §4 layering line "(NO card here — card is domain)."** The generic
> `TheKrystalShip.Llm` package *does* now carry the card — but only as an uninterpreted `object?`
> passenger (`ToolOutput.Data` / `AgentEvent.ToolData`); it never takes a domain dependency.
>
> **Versioning:** bumped `TheKrystalShip.Llm` 1.0.0 → **1.1.0**. Strict SemVer would call a
> return-type change on a published interface (`Task<string>` → `Task<ToolOutput>`) **major
> (2.0.0)**. We bump **minor** deliberately: every consumer in the workspace is a
> `ProjectReference` (kgsm-llm Assistant/Service/Cli, kgsm-bot), the nupkg has **no external
> consumers**, and the implicit `string → ToolOutput` keeps summary-only call sites
> source-compatible. The caveat, recorded so it isn't a surprise: if anyone ever pins the package
> by version, the minor bump hides a breaking interface change — revisit to 2.0.0 if it gains an
> external consumer.

**Scope — honestly narrow (one real card, not eight).** The original Phase-2 list named eight
§5·b "card tools" (`get_server_status`, `get_performance`, `get_console`, `get_config`,
`get_host_diagnostics`, `get_network`, `run_health_check`, `trace_root_cause`). **Five of those
don't exist as implemented tools, and single-server `get_status`/`read_file` have no structured
source.** The tools that produce a real `ToolResult<TData>` today are **`run_health_check`**
(`HealthCheckAggregator → HealthData`), **`get_status` fleet mode** (`FleetStatusCard → FleetStatusData`),
**`search`** (`SearchAggregator → SearchData`), and **`get_performance`**
(`PerformanceReport → PerformanceData` — a live snapshot for a running server, or a windowed history
series for a trend read; carded only when there are measured values / a non-empty series, else
summary-only). Building cards for absent tools would fabricate data — forbidden.

**Adding the next card** is one line **in its handler** — `return new ToolOutput(summary,
ToolResultCard.From(theResult))` — *for a tool that already produces a `ToolResult<TData>`*. A
tool that returns a bare string today (most of them) needs its card type + builder built **first**
(then the one-liner). The rail (transport, serialisation, relay, SPA contract) does not change for
any of them.

**Second card lit: `get_status` fleet mode (DONE 2026-06-19).** `Status/FleetStatusCard.cs` is a
pure builder (mirrors `HealthCheckAggregator`) projecting the neutral `FleetStatusEntry[]` into a
`FleetStatusData` card — `{ servers[], total, running, stopped, unavailable }`, each server
`{ instance, state, severity, reason? }`. The load-bearing rule is the same never-collapse
doctrine as health: an instance whose status could **not be read** maps to `ServerRunState.Unknown`
/ `Severity.Warn` with its reason — **never** `Stopped` (a bare "not running" would masquerade as a
measured fact). Counts are honest (the unreadable one lands in `unavailable`, never inflating
`stopped`). The `Summary` stays **byte-identical** to the dispatcher's prior inline string (model
grounding unchanged); an **empty fleet** is a real measured result → an empty card (not dropped); a
read **failure** stays summary-only (no card). `ServerRunState` serialises camelCase for free via
the converter already added — **zero rail changes**.

- **`get_status` card-kind note for the SPA:** `result.tool == "get_status"` ⇒ the **fleet** card
  shape (`FleetStatusData`). The **single-server** `get_status` (with an `instance_name`) returns
  kgsm's opaque status string — no structured source — so it is **cardless** (summary only). One
  tool name, one card shape; the single-server detail view renders off `summary`.

- **Phase 1 (shipped):** `tool.result` carried `{ id, tool, summary }`. Documented divergence:
  §5·a names the field `result`; we emitted only `summary` until the card landed.
- **Phase 2 (shipped):** `tool.result` carries `{ id, tool, summary, result? }` — `summary` always
  (Phase-1 clients unaffected), `result` present only when the tool has a card (today: health).

### WI-6 · envelope + key normalisation — **cheap** (Service SSE writer)
- Emit an in-band **`type`** field on every event's `data` (matching `architecture.html`'s
  example), **and keep the SSE `event:` name** (a superset — a client can key on either).
- Rename payload keys to §5·a: `text.delta`/`thinking.delta` `delta`→`text`.
- Centralise the event-name/`type` strings (don't scatter literals) — mirror the API side's
  `StreamProtocol.cs` discipline.

### WI-7 · `tool.start` `label` — **omit + derive** (honesty call)
The `Tool` model is `record Tool(string Name)` — **no display label, no honest source.**
Per the never-guess rule (and the deferred metadata-curation note), **do not fabricate a
label.** Two options for sign-off (§7):
- **(default) Omit `label`; the SPA derives it** from the tool name or the `GET /tools`
  description it already fetches. Documented divergence from the §5·a example.
- **(later) Curated `label`** added to each tool definition (a real, human-authored source) —
  a small metadata-curation pass, deferrable past M7.

---

## 4 · Layering (where each change lives)

```
TheKrystalShip.Llm  (generic, domain-agnostic)
  └─ AgentEvent gains ToolCallId          ← WI-4 (a tool-call id is a generic LLM concept)
     LlmAgent mints + pairs the id        ← WI-4
     ToolOutput(Summary, object? Data)    ← WI-5  (IToolDispatcher returns it; implicit string→ToolOutput)
     AgentEvent gains object? ToolData    ← WI-5  (an OPAQUE passenger — loop never inspects it)
     LlmAgent threads outputs[i].Data → ToolResult event (index-aligned by Task.WhenAll order)

TheKrystalShip.Kgsm.Assistant  (KGSM domain)
  └─ AssistantStreamEvent gains ToolCallId + object? ToolData   ← WI-4, WI-5
     Envelope/ToolResultCard.cs: non-generic projection of ToolResult<TData> (Summary dropped)  ← WI-5
     ToolDispatcher.RunHealthCheckAsync returns ToolOutput(summary, ToolResultCard.From(health)) ← WI-5
     ServerAssistant forwards ev.ToolData verbatim (relays, never inspects)  ← WI-5

TheKrystalShip.Kgsm.Assistant.Service  (the SSE boundary — most shaping here)
  └─ SseTurnWriter: type+key normalisation, command.proposed
     reshape, error split, thinking key  ← WI-1, WI-2, WI-3, WI-6
     SseTurnWriter: tool.result `result` = ev.ToolData + JsonStringEnumConverter  ← WI-5
     Contracts.cs: ToolResultEvent gains `object? Result` ([JsonIgnore WhenWritingNull])  ← WI-5
```

---

## 5 · Frozen target wire shapes (the §5·a realisation)

```
event: text.delta
data: { "type":"text.delta", "text":"Checking factorio-test…" }

event: thinking.delta                                  # additive, opt-in (think:true)
data: { "type":"thinking.delta", "text":"The crash log mentions…" }

event: tool.start
data: { "type":"tool.start", "id":"tc_0_0", "tool":"trace_root_cause",
        "arguments": { "server_id":"factorio-test" } }     # label omitted (WI-7); arguments additive

event: tool.result                                     # summary always; `result` card when the tool has one
data: { "type":"tool.result", "id":"tc_0_0", "tool":"run_health_check",
        "summary":"factorio: passed with warnings. …",
        "result": {                                    # Phase 2 — ToolResultCard (enums as camelCase strings)
          "tool":"run_health_check", "confidence":"confirmed",
          "subject": { "resource":"server", "id":"factorio", "section":null },
          "data": { "overall":"warn",
                    "checks": [ { "name":"liveness","state":"pass","severity":"success","detail":"Running." },
                                { "name":"updates","state":"warn","severity":"update","detail":"Update available." } ],
                    "passed":1, "total":2, "skipped":0 },
          "links":null } }
                                                       # a summary-only tool omits `result` entirely (not null)

event: command.proposed
data: { "type":"command.proposed", "id":"cmd_0", "verb":"start",
        "subject": { "resource":"server", "id":"factorio-test" },
        "confirm":"Start factorio-test?",
        "token":"<opaque>" }                               # token additive (for /confirm surfaces)

event: error
data: { "type":"error", "code":"assistant_failed", "message":"…" }

event: done
data: { "type":"done", "text":"<full reply>", "usage": { … } }   # text/usage additive
```

`command.verified` is intentionally **absent** from the turn stream (§6).

---

## 6 · Fork (a) consequences — execution & the verb→API matrix

The assistant emits `command.proposed` for **every** staged kind. How the SPA *executes* a
confirmed proposal depends on whether the API exposes that verb. The SPA routes to the API
where it can; the assistant's `/confirm` (carried by the retained `token`) covers the rest and
the non-API surfaces.

> **Auto-accept (2026-06-26) — the one exception to "propose-only".** A turn the API marks
> `X-Relay-Auto-Act: true` (verified **admin** tier ∧ the SPA's per-turn "Auto-run" toggle —
> strictly stronger than `X-Relay-Can-Act`, which is now operator+ and toggle-independent) lets the
> assistant **run the lifecycle verbs immediately** (`start`/`stop`/`restart`/`update`/`backup`)
> instead of staging them. Such a verb emits **no `command.proposed`** — it runs in the dispatcher
> via `IServerOperations` and surfaces as the ordinary `tool.start`/`tool.result` pair (the
> result string is the real outcome), so the SPA needs no new frame. Install / uninstall /
> set-config stay propose-only even on an auto-accept turn. The admin gate is enforced API-side
> (`AssistantController` folds tier ∧ toggle); the SPA toggle is UX only (visible to operator+,
> enabled for admin). This is the assistant executing inline — distinct from fork (a)'s SPA→M3
> path below, which still serves every non-auto turn.

| Confirmation kind | §5·a `verb` | SPA execution path | Available |
|---|---|---|---|
| Start / Stop / Restart | `start`/`stop`/`restart` | `POST /servers/{id}/commands` (M3) | ✅ now |
| (firewall) | `open_ports` | `POST /servers/{id}/commands` (M6·b) | ✅ now |
| Update | `update` | `POST /servers/{id}/commands` | ⏳ M3 `update` deferred |
| Install / Uninstall | `install`/`uninstall` | `POST /servers` + uninstall (M8) | ⏳ M8 |
| Backup | `backup` | no API endpoint yet | ⏳ future verb |
| SetConfig | `set_config` | no API endpoint yet (config write) | ⏳ M8-ish |

- **`command.verified { id, ok, headline, lines[] }` is NOT emitted by any backend stream**
  — not the turn relay, not the M3 path. It is **composed client-side by the SPA**, which
  (under fork (a)) owns both the proposal render and the M3 call, from three sources it
  already receives:
  | §5·a field | Honest source on the M3 path |
  |---|---|
  | `id` | the SPA's own `cmd_<n>`→`job_<x>` mapping — **maintained client-side; no id round-trips the backend** (see below) |
  | `ok` | the `jobs` topic `job.patch` outcome (`succeeded`/`failed`) + the verify `server.patch` (status reached) |
  | `headline` | the command's **`audit.append` summary** (e.g. `"started factorio-test"` — `AuditMapping` emits `"{verb} {instance}"`; richer than `server.patch`, already a WS topic) |
  | `lines[]` | **honest-thin** — no rich multi-line source on the M3 path; the audit `meta` is action-specific and usually sparse (e.g. an `update`'s `{oldVersion,newVersion}` yields a line; `start`/`stop`/`restart` yield none). Render what's there, omit the rest — **never fabricated** (same rule as `reachable`/`cpuPctCore`). A richer `command.verified` is a future enhancement if a real source appears. |
- **proposed→verified `id` correlation:** §5·a intends `command.verified.id == command.proposed.id`
  (resolve the button → the verification block). The assistant mints `cmd_<n>` in the turn
  stream; the M3 path never sees it. **The SPA correlates locally** — it rendered the proposal
  and issued the M3 call, so it maps `cmd_<n> → job_<x> → verified` client-side. No correlation
  id round-trips the backend; this is a frontend-state concern, recorded here so the gate
  doesn't surprise on it.
- **Retained `token`** keeps Discord, the CLI, and any not-yet-API-backed verb working via the
  assistant's `/confirm` — additive to §5·a, removed for no one.
- The assistant's `/confirm` endpoint is **not deleted**. Fork (a) governs *the SPA's*
  execution of API-backed verbs, not the existence of the assistant's own confirm path.

---

## 7 · Open questions for frontend sign-off

1. **Envelope reader:** does the SPA key on the SSE `event:` name or the in-band `data.type`?
   (We emit both — confirm which the store reads so we can drop the other later if desired.)
2. **`label`:** accept omit-and-derive (WI-7 default), or do you want a curated per-tool label?
3. **`tool.result` Phase 1:** is rendering off `summary` (string) acceptable until the §5·c
   cards land per tool (WI-5 Phase 2)? Confirm the card rollout order.
4. **`command.proposed.token`:** acknowledge it as an additive field the SPA ignores (it routes
   to M3) but Discord/CLI use.
5. **`error.code` vocabulary:** agree the closed set.
6. **`command.verified` is SPA-composed** (§6): confirm the frontend owns the `cmd_<n>→job→verified`
   correlation client-side and accepts an **honest-thin `lines[]`** (often empty for fast verbs;
   populated only where the audit `meta` has real detail, e.g. an update's version delta).
7. **Host-scoped card subject** (Phase 2, `get_status` fleet card): a host-wide card has no single
   instance subject, so it uses `subject: { resource:"host", id:"primary" }` — matching
   `architecture.html`'s `hostId:"primary"` self-reference convention ("denormalized; derived if
   absent"). The id is **informational** (the per-host panel already knows its host), not a route
   key. Confirm the SPA reads it that way, or name the token it wants the leaf to emit (the assistant
   is a standalone leaf and can't borrow the API's real `hostId`). Same applies to any future
   host/audit/metrics-scoped card.

---

## 8 · Sequencing

- **Phase 1 (lands the §5·a envelope):** WI-1, WI-2, WI-3, WI-4, WI-6, WI-7-default. After
  this, kgsm-api M7 can build the near-verbatim relay; `tool.result` carries `summary`.
  **✅ DONE 2026-06-19** — `AgentEvent`/`AssistantStreamEvent` carry a synthesised `ToolCallId`
  (`LlmAgent` mints `tc_<n>`, turn-level, pairs start↔result); the Service `SseTurnWriter`/`Contracts`
  emit the §5·a frames (in-band `type` + `event:` name, `delta`→`text`, tool-call `id`,
  `command.proposed {id,verb,subject,confirm,token,…}`, `error {code,message}`). Tests: id-pairing
  in `LlmAgentStreamTests`, wire-shape (incl. the error reshape) in `EndpointSmokeTests`. Buffered
  `/turn` (`ConfirmationDto`/`TurnResponse`) deliberately left unchanged. 345/345 green + kgsm-bot clean.
- **Phase 2 (cards):** WI-5 — the opaque-payload card channel, plumbed end-to-end. **✅ DONE
  2026-06-19.** `IToolDispatcher.ExecuteAsync → ToolOutput(Summary, object? Data)` (implicit
  `string→ToolOutput`, package 1.0.0→1.1.0); `AgentEvent`/`AssistantStreamEvent` carry an opaque
  `object? ToolData` (index-aligned by `Task.WhenAll` order, never inspected by the loop, never
  shown to the model); `Envelope/ToolResultCard.cs` projects `ToolResult<TData>` (Summary
  dropped); two tools have a real card lit (each with a structured source):
  `run_health_check` (`ToolResultCard.From(HealthCheckAggregator.Run(…))` → `HealthData`) and
  **`get_status` fleet mode** (`Status/FleetStatusCard.cs` → `FleetStatusData`); the SSE
  `tool.result` frame carries `summary` (always) + `result?` (the card, enums as camelCase
  strings). Tests: dispatcher card surfacing + opaque flow-through (`ToolDispatcherTests`,
  `ServerAssistantStreamTests`, `LlmAgentStreamTests`) + the Service-boundary frame shape
  (`EndpointSmokeTests`) + the fleet builder's never-collapse-Unavailable invariant, byte-identical
  summary, and empty-card (`FleetStatusCardTests`); single-server `get_status` stays cardless.
  **358/358 green + kgsm-bot 49/49.** Future tools light their card with a one-line handler change
  once they have a structured source.

## 9 · Test impact (kgsm-llm)

- `Service.Tests/GetStatusLiveTests.cs` (`FleetPrompt_StreamsCanonicalTypedEvents`) — update
  event-shape assertions (`type`, `text`, tool-call `id`).
- `TheKrystalShip.Llm.Tests/LlmAgentStreamTests.cs` — assert `ToolCallId` pairs start↔result.
- `TheKrystalShip.Kgsm.Assistant.Tests/ServerAssistantStreamTests.cs` — confirmation reshape +
  (Phase 2) card capture.
- Add SSE-shape assertions at the Service boundary (the `SseTurnWriter` output frames).

## 10 · Cross-references

- `../../architecture.html §5·a` — the frontend wire contract (authority).
- `../../assistant-toolbox-plan.md §5·a/§5·d` — canonical vocabulary; `command.verified` origin.
- `../../kgsm-api/PLAN.md` M7 + §6 registry — the relay milestone + contract freeze row.
- `../../system-architecture.md` O1 — "proxy/relay verbatim" enabled by canonical emission.
</content>
</invoke>
