# KGSM Ecosystem — Architecture Review Findings

> Cross-repo review, 2026-06-11. Companion to `system-architecture.md` (the keystone
> *map*); this doc holds the *audit* — concrete findings, evidence, and fixes.
> Scope: the live/near-live C# spine (`kgsm`, `kgsm-lib`, `kgsm-monitor`, `kgsm-llm`,
> `kgsm-bot`). Deprecated repos (`kgsm-api`, `kgsm-web`) are excluded except as noted.

## Thesis: the chokepoint is right; it's operated by hand

Centralizing all C#↔kgsm traffic through `kgsm-lib` is the correct pattern and is what
makes "cohesive whole" reachable. The gaps are **not** architectural — they're the
missing operational shell around the chokepoint (no CI, no publish step, no contract
test). That single absence produces both observed symptoms:

> no pipeline around `kgsm-lib` → the lib can't propagate → consumers pin a stale build
> (**own-legs failure**) **and** nothing checks the bash↔C# contract → the event
> vocabulary silently drifts (**cohesion failure**).

Fix how the chokepoint is *operated* and most of the rest follows. None of this argues
for an added message bus — the gaps are pipeline + contract-testing, not transport.

---

## Findings (priority order)

### 1. Today's lib fixes are stranded — the lib doesn't reach two consumers  · HIGH

Split-brain dependency model:

| Consumer | Mechanism | Pinned to |
|---|---|---|
| `kgsm-monitor` | `ProjectReference` → `../../../kgsm-lib/kgsm-lib/kgsm-lib.csproj` | source on disk (1.0.0→1.1.0) |
| `kgsm-bot` (3 layers) | `PackageReference TheKrystalShip.KGSM.Lib` | **`1.0.0-beta`** (2025 pre-release) |
| `kgsm-llm` (assistant service) | `PackageReference TheKrystalShip.KGSM.Lib` | **`1.0.0-beta`** |

The on-disk lib is `1.0.0`/`1.1.0`, but those `.nupkg`s were only built locally; **only
`1.0.0-beta` was ever published**. So the `recent_logs` deserialization fix and the
`GetAllStatuses` fan-out fix shipped this cycle **cannot reach the bot or the assistant**
until the lib is published and the pins bumped. The assistant isn't broken — the
improvement just can't be delivered through the current distribution. The monitor's
relative-path `ProjectReference` also breaks the other direction: it can't build on a
clean CI runner without the whole sibling tree.

**Fix (pick one, consistently; kill the `1.0.0-beta` pins either way):**
- *Published versioned package* — every consumer (monitor included) references a real
  version; cross-repo changes become explicit bumps. Precondition already satisfied:
  `kgsm-lib.csproj` sets `<IsAotCompatible>true</IsAotCompatible>`, so the monitor keeps
  its AOT/trim analysis when consuming the package. **(Recommended — matches "own legs".)**
- *Standardize on `ProjectReference`* (monorepo-style): always-latest, atomic edits, at
  the cost of isolated buildability.

> **RESOLVED 2026-06-12 — published + all three consumers migrated to the package.**
> `1.1.0` was published to nuget.org (manually via the web upload; the `dotnet nuget push`
> with `NUGET_AUTH_HEADER` 401'd — the var was empty in the shell). Then every consumer was
> moved onto `PackageReference 1.1.0` (the monitor off its source `ProjectReference`, the bot
> + assistant off the `1.0.0-beta` pin) and rebuilt+tested green: **monitor** 60/60 + clean
> AOT/ILC publish; **assistant** 112/112; **bot** 4/4 + full solution clean.
>
> **The migration's real lesson — `1.1.0` is NOT backward-compatible with the published
> `1.0.0-beta`, despite its release notes saying "Backward compatible with 1.0.0."** A
> minor-version bump silently shipped source-breaking changes; only the **monitor** swapped
> in clean, *because it already tracked current lib source* — the two **package** consumers
> (the exact ones this finding is about) each needed real porting:
> - **Dependency floor:** M.E.* raised `8.0.0`→`9.0.0` (`InstanceService`/`DI`), so every
>   `PackageReference` consumer hit `NU1605` until its own M.E.* refs were bumped (bot: 11 of them).
> - **Namespace move:** `InstanceStatus` → `…Core.Models.Enums` (bot: +`using` in 5 files).
> - **New colliding type:** `…Core.Models.KgsmOptions` added → name-collided with the bot's
>   own `KgsmOptions` (resolved with a per-file `using`-alias).
> - **Removed/renamed API:** `IBlueprintService.GetAll()` → `ListDetailed()` (bot + assistant);
>   `IBlueprintService.Create()` **removed outright** — the lib carries no blueprint-create code at
>   all and kgsm exposes no command to create blueprints, so the bot's dead `CreateAsync` was
>   deleted from its interface + impl (2026-06-12), not stubbed.
> - **Changed shapes:** `InstanceService`/`BlueprintService` constructors gained `ILogger`/`KgsmOptions?`
>   (assistant rewired its manual DI to the `IKgsmCommandExecutor` graph, still socket-free);
>   `Instance.Blueprint` is now a computed get-only (`=> GetFileNameWithoutExtension(BlueprintFile)`)
>   and `Instance.Directory` was replaced by `WorkingDir`/`InstallDir`/… .
>
> Also pre-existing and unrelated to the lib: the bot's `TheKrystalShip.Llm`/`Kgsm.Assistant`
> `ProjectReference`s pointed at a sibling `…/TheKrystalShip.Llm/` that doesn't exist in this
> workspace (the code lives in `kgsm-llm/`); repointed to `..\..\..\kgsm-llm\…`.
>
> **Forward lesson (ties to finding #3 / no-CI):** the breaking changes warranted a **major**
> bump, not `1.1.0`, and the "backward compatible" release note is inaccurate — a contract test
> + a CI build of the consumers against a candidate package would have caught both before publish.
> All consumer changes are **uncommitted**, one diff per repo, pending review.

### 2. The bash↔C# event contract is hand-maintained in THREE places and has drifted  · HIGH  · **RESOLVED 2026-06-11**

The event vocabulary lives in three lists that must agree:
- **(a)** bash *call sites* — `core/events.sh` dispatcher + command handlers calling `events.sh emit <name>`
- **(b)** bash *registry* — `EVENT_CONFIGS` assoc. array, `commands/handlers/events.sh` (was 27 events)
- **(c)** C# *mapping* — `_eventTypeMapping`, `kgsm-lib/.../Services/EventService.cs` + the `EventDataBase` subclasses in `Events/EventTypes.cs`

Wire `EventType` is **underscored** (`_build_event_payload` runs `${name//-/_}`,
`commands/events.sh`), and (b)/(c) matched for the 27 *registered* events — but two
distinct drifts made events silently vanish, both verified empirically against the live
validation logic:

1. **Call sites (a) emitting names the registry (b) never registered** — `instance-restarted`
   and the three `*-failed` events. Their emit fails the name→type validation in
   `__logic_event_name_to_type` (`commands/handlers/events.sh`) and **aborts silently inside
   bash**; nothing errors because the underlying operation already succeeded.
2. **A registered event whose call site under-supplies params** — `instance-started`'s spec
   was `"instance lifecycle_manager"` but `_cmd_start` passed only the instance → param-count
   validation rejected it (rc=38) → **`instance-started` never fired either.** And
   `instance-stopped` only "passed" by emitting a **fabricated** `lifecycle_manager="standalone"`
   (the `${instance_lifecycle_manager:-standalone}` global was never loaded in the command
   path) — a "never fabricate" violation.

| Event | Before | Fix shipped |
|---|---|---|
| `instance-started` | registered, but emit aborted (missing `lifecycle_manager`) | ✅ command resolves the **real** lifecycle_manager and passes it |
| `instance-stopped` | emitted a **fabricated** `standalone` | ✅ now passes the real value (no fabrication) |
| `instance-restarted` | unregistered → never fired | ✅ registered (`instance lifecycle_manager`) + `InstanceRestartedData` |
| `instance-download-failed` | unregistered → never fired | ✅ registered (`instance`) + `InstanceDownloadFailedData` |
| `instance-deploy-failed` | unregistered → never fired | ✅ registered (`instance`) + `InstanceDeployFailedData` |
| `instance-uninstall-failed` | unregistered → never fired | ✅ registered (`instance`) + `InstanceUninstallFailedData` |
| `system-restart` / `system-shutdown` | dispatcher TODO-stubbed, never emitted | ⏸ deferred (global-event infra not ready) |

Blast radius (now closed): restart, start, and every failure event were invisible to all
consumers — the bot couldn't announce "your install failed"; the future web API couldn't
push a failure toast to the SPA.

**What shipped (kgsm + kgsm-lib, 2026-06-11):**
- kgsm: 4 `EVENT_*` constants + `EVENT_CONFIGS` specs; `instance_restarted` added to the
  `LifecycleManager`-carrying payload case in `_build_event_payload`; new exported
  `__resolve_lifecycle_manager <name>` (config-driven, **no fabricated fallback**) wired into
  `_cmd_start`/`_cmd_stop`/`_cmd_restart`.
- kgsm-lib: 4 `EventDataBase` subclasses (`InstanceRestartedData` carries `LifecycleManager`;
  the `*-failed` carry instance only) + `KgsmJsonContext` `[JsonSerializable]` registrations +
  `_eventTypeMapping` entries.
- **Conformance guards added** (the durable payoff — cover all three drift directions):
  - kgsm `test_all_emit_call_sites_are_registered` — every `events.sh emit` site must be a
    registered event (catches **call-site↔registry** drift, the original incident).
  - kgsm-lib `EveryEventDataType_IsRegisteredInJsonContext` — every event type must be
    `[JsonSerializable]` (catches the **type↔JsonContext** AOT trap).
  - kgsm-lib `EveryEventDataType_HasAMappingEntry` — every event type must be in
    `_eventTypeMapping` (catches a defined-but-unmapped type → dropped as "Unknown event
    type"; always-on, no sibling repo needed).
  - kgsm-lib `BashEventRegistry_IsSubsetOf_CSharpMapping` — parses the sibling kgsm
    `EVENT_CONFIGS` and asserts ⊆ `_eventTypeMapping.Keys` (the true **bash-registry↔C#-mapping**
    equality, incl. key typos; runs when the repos are colocated, no-ops standalone).
  - Wire-shape pinned by `EventDeserializationTests` (real payloads → typed data). Existing
    "all 27" tests updated to 31.
- Verified: all 6 events pass validation; real `instance_started`/`instance_restarted`/
  `instance_download_failed` payloads captured with correct PascalCase keys + real
  lifecycle_manager; kgsm event/lifecycle suites green; kgsm-lib at baseline (+3 new, 0 new
  failures); Release/AOT build 0 warnings.

> **Still pending (finding #1 coupling):** these fixes only reach the bot/assistant once
> kgsm-lib is **published and their `1.0.0-beta` pins are bumped**. The monitor (source
> `ProjectReference`) gets them on next build.

### 3. No CI / publish pipeline anywhere — the enabler for #1 and #2  · HIGH  · **baseline cleaned 2026-06-12**

Zero `.github/workflows` in any repo; no publish-on-tag for the lib. This is *why* #1 and
#2 persist. Minimal per-repo CI (build + test, plus publish-on-tag for the lib) is the
lever — proportional to a few-host project, **not** a deployment platform.

> **Update (2026-06-12):** the lib's *degraded test baseline* — the ~20 known-failing /
> 11 skipped tests that normalized red — is **fixed**. Root cause was test rot, not product
> defects: stale `IProcessRunner` mocks left over from the `IKgsmCommandExecutor` refactor
> (never wired in, so the service returned defaults), two inverted-assertion "landmine" tests
> made green by matching buggy behavior, wrong exception-type asserts, and dead tests for a
> removed `Create` API. Cleaned to **407 passing / 0 failing / 0 skipped**; added fixtures for
> previously-untested `LifecycleService`/`FileService`/`WatcherService`/`EventService`
> (dispatch route); `InstanceService` facade now validates inputs uniformly; lib bumped to
> **1.1.0** (`.nupkg` built locally). Socket/process-bound `LogSubscriptionService` &
> `UnixSocketClient` deliberately deferred to an integration category. CI itself still does
> not exist — that's the remaining open part of this finding.

### 4. The bash↔C# **command** vocabulary can drift like the events did — one live instance  · MED  · found 2026-06-12

Same root cause as #2 (hand-maintained contract, nothing checks it), different surface: the
*CLI command strings* kgsm-lib issues are pinned only by tests that mock the executor, so
nothing verifies they're commands kgsm actually accepts. Auditing every command string in
the lib against kgsm's live dispatch (`commands/*.sh` case arms) turned up **one real drift**:
`FileService.CreateConfig`/`RemoveConfig` issue `files config install|uninstall`, but kgsm
**removed** the standalone `files config` component (kgsm commit `75644d7` deleted
`files.config.sh`; config-file handling folded into `files create`/`remove`). Those two
methods hit kgsm's "Unknown component" path and always fail. **Fixed (lib):** both marked
`[Obsolete]` (non-breaking for the 1.1.0 bump) + a reflection guard test documents the drift;
remove at the next major. **Durable guard worth adding** (analogous to the event
`BashEventRegistry_IsSubsetOf_CSharpMapping` test): parse kgsm's command case-arms and assert
every command string the lib issues is routable — converts the next command drift from a
silent always-fail into a red test. kgsm's own `files.sh` help text still advertises the
removed `config` component (stale help, separate small kgsm cleanup).

### 5. kgsm `system` / `network` modules vs the ready monitor — verdict: reframe ownership, don't remove  · LOW/INFO  · 2026-06-12

Triggered by `kgsm-monitor` reaching a ready state: should kgsm's `system` and `network`
modules be removed now that the monitor surfaces host metrics? **Audit verdict: keep almost
everything; the overlap is narrow, not total.**

**The framing to correct first.** The monitor is *not* "the same metrics, more efficiently."
It is a **stateful, self-ticking daemon** producing continuous, **rate-based**, **per-server**
(cgroup v2 / `/proc`-tree) samples served over a socket. kgsm `system` is a **stateless,
zero-dependency, one-shot** host read — plus host **actions**. For "what is memory *right
now*," shelling `free` needs no daemon and no socket; it is arguably the *more* efficient path.
The monitor's genuine win is continuous / rate / per-server signal — which kgsm never produced.
So the two are largely **complementary**, and the assistant plan already splits them that way
(host snapshot vs per-instance time-series).

**Decision rule used:** *does the monitor's `Snapshot` record (`kgsm-monitor/.../Model/Snapshot.cs`)
actually carry this field?* That splits the two "modules" into four buckets:

| Bucket | In `Snapshot`? | Verdict |
|---|---|---|
| `core/system.sh` (`__create_dir`/`__source`/`__create_file`) | — | **Out of scope.** Foundational fs/source helpers other modules depend on; nothing to do with metrics. A *different file that shares the name* — don't conflate with `commands/system.sh`. **Keep.** |
| `network` (whole module) | No | **Keep wholesale.** `Snapshot` carries net **throughput** (`InterfaceRate` Rx/Tx Bps/Pps); kgsm `network` is **port management** (`ports check/list-used/conflicts/kill`, `test-port/test-all`, `ip`, `dns`). **Zero field overlap.** Backs the live `INetworkService` and the planned assistant `get_network`/`open_ports`. |
| `system` power-mgmt: `shutdown`/`restart`/`cancel` + `reboot-required` | No | **Keep.** Host **actions** + a boolean state check, not resource metrics. Monitor does not (and should not) do these. |
| `system` metric-reads: `uptime`/`load`/`memory`/`disk`/`info[ --json]` | **Yes** | **The only real overlap.** `Snapshot` covers `UptimeSec`, `CpuMetrics.Load`, `MemoryMetrics`, `DiskMetrics` — and is strictly richer (per-core, swap, disk IO bps, all mounts, *per-server*). **But** `info` also bundles `reboot_required` + `external_ip`/`local_ips`, which the monitor does **not** carry, so even `info` is not fully replaceable. |

**The lone decision — and it's the owner's, not to be auto-applied.** The four `system`
metric-reads are the only deprecation candidate. Two honesty caveats before recommending it:
1. **They work and read honest sources** (`free`/`df`/`uptime`/`/proc/loadavg`). Keystone §9
   explicitly calls `system info --json` the *correct* host baseline the scrapped `kgsm-api`
   failed to match. So this would be obsolete-*because-superseded*, **not**
   obsolete-*because-broken* — a different bar from the `files config` drift in #4. Do **not**
   reflex-apply `[Obsolete]` to working, shipped API.
2. **They are public `kgsm-lib` API shipped in 1.1.0 last cycle** (`ISystemService.GetUptime/
   GetLoad/GetMemory/GetDisk/GetInfo/GetInfo<T>`). Delete = breaking change → major bump +
   assistant host-diagnostics rewire. `[Obsolete]`-mark = committing the monitor as the *sole*
   host-metric source.

**No urgency either way — nothing live consumes either side today.** The monitor is live
(host) but consumes the lib **only** via `IInstanceService.GetAll()` for inventory
(`ServerSampler.cs:197`); it never calls `ISystemService`/`INetworkService`. The aggregating
web API + SPA are unbuilt; the assistant that references `system info --json` for
`get_host_diagnostics` is *planned*, not built. So there is no live breakage to fear and no
deadline forcing the call.

**Recommendation.** Keep `core/system.sh`, the whole `network` module, and `system`
power-management. The only change worth making is **on the consumer side, not in kgsm**: make
the monitor the source of truth for *continuous* host metrics in the consumers (assistant
`get_host_diagnostics`, the future web API) and treat kgsm's `system` reads as the
zero-dependency one-shot fallback. Update the `assistant-toolbox-plan.md` line "host snapshots
still come from KGSM `system info`" to prefer the monitor now that it is ready. Only if the
owner commits to monitor-as-sole-source do the four reads earn a non-breaking `[Obsolete]`
ahead of removal at the next major. **Ownership boundary, one line:** *the monitor owns
continuous / per-server / rate host+server metrics; kgsm `system` owns one-shot host actions +
a zero-dependency host snapshot fallback.*

---

## Lower priority

- **Socket-path defaults disagree** — bot `/opt/kgsm/kgsm.sock`, old api `/tmp/kgsm.sock`,
  kgsm `$KGSM_ROOT/kgsm.sock`, monitor `/run/kgsm-monitor*.sock`. All configurable, but
  mismatched defaults fail silently. A shared convention (`/run/kgsm/*.sock` or a tiny
  `/etc/kgsm/endpoints`) buys cohesion cheaply.
- **Only `kgsm-monitor` has a systemd unit** — "how you run this in prod" is undefined for
  the bot and assistant. One unit file each.
- **Secret hygiene (minor, no leak today)** — `kgsm-llm` tracks `appsettings.json` with
  *empty* secret slots; invites a future accidental commit. Move to env/user-secrets like
  the bot already does (bot's real token is correctly gitignored).
- **Forward lesson (not a live bug)** — `kgsm-api` re-samples `/proc` itself instead of
  consuming the monitor. `kgsm-api` is being scrapped; when the *real* web API is built it
  must consume the monitor, never re-measure (same "never fabricate a metric" rule).

---

## "Own legs vs cohesive whole"

The two goals pull against each other and the chokepoint is the resolution: a versioned
`kgsm-lib` package is the seam that lets each project build/test/deploy alone (own legs)
while guaranteeing they all speak one validated contract (cohesive). The hard part is
built; what's missing is the boring operational shell around it.
