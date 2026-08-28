# Player Presence — design + frozen contract (container parity, Increment 1)

**Status:** design frozen + advisor-vetted, 2026-06-19. Implementation in progress. Companion to memory
`kgsm-player-presence-tracking.md`. This is the contract the 3 implementation repos build against verbatim.

## Goal & scope

Track online player presence and surface it on the existing event stream → kgsm-lib → kgsm-api audit →
notification `join` catalog event (the honest source it currently lacks). **Increment 1 = container
parity, JOIN/LEFT ONLY**, via **Path 2 (in-image shim self-reports)**.

**Explicitly OUT of Increment 1** (advisor: keep the first cut purely additive / low-blast-radius):
- **Readiness migration** — `instance_ready` is an EXISTING event with live consumers; moving its
  emitter is higher-stakes, sequence it later. The shim's matcher is built *general* (a list of
  pattern→event rules) so adding a `ready` rule later is config, not rework — but it is **unwired** now.
- kick/ban; native stream-matcher; kgsm-api audit→notification mapping (downstream consumer, follows
  once events flow); query/A2S fallback; BYO-image detection.

## Parity principle

Parity is at the **EVENT CONTRACT**, not the mechanism. Honest boundary: parity holds for **our
kgsm-containers images** (we add the shim). Arbitrary BYO third-party images → honest `unknown`, never faked.

## Established facts (drove the decisions below)

- **No resident kgsm event broadcaster.** `kgsm events emit` → `socat - UNIX-CONNECT` (fire-and-forget
  client); the *consumers* (kgsm-lib/api/bot) LISTEN. So nothing resident in kgsm can host ingestion.
- **kgsm-lib emit is STRING-based:** `IEventManagementService.EmitWithProvenance(string eventType,
  string? actor, string? origin, params string[] parameters)`. The watchdog already emits via this with
  string consts. ⇒ **new events need NO kgsm-lib code change**; the watchdog compiles independently.

---

## FROZEN CONTRACT (all 3 repos build against this verbatim)

### A. Wire events (kgsm event vocabulary — defined in kgsm bash)

| event | params | notes |
|---|---|---|
| `instance_player_joined` | `instance` (req), `player_id` (str\|null), `player_name` (str\|null) | **at least one of id/name non-null** |
| `instance_player_left`   | `instance` (req), `player_id` (str\|null), `player_name` (str\|null) | same |

- `player_id` = opaque, **game-scoped** stable id when available (SteamID64 / MC UUID). No `id_type`.
  `player_name` = display label. JSON `null` when a source gives only one. NEVER fabricate the missing one.
- Provenance on container-originated events: **`actor = "system"`, `origin = "system"`** (system/system,
  mirroring the watchdog's existing autonomous emits). Do NOT pass `actor:null` — kgsm-lib omits the
  actor env when null and kgsm's `_build_event_payload` then falls back to the daemon's OS user
  (`${SUDO_USER:-${USER:-}}`→`id -un`), i.e. a fabricated HUMAN identity on an autonomous event. The
  literal `"system"` is what suppresses that fallback (watchdog caught this during impl).

### B. In-container → host event channel (shim writes; watchdog reads)

- **Transport:** append-only **NDJSON** file (one JSON object per line).
- **In-container path:** `/run/kgsm/events.ndjson`, where **`/run/kgsm` is a DIRECTORY bind-mount**
  (NOT a single-file mount — a file mount pins the host inode for the mount's lifetime, so the shim
  could never surface a fresh inode on restart; and Docker would auto-create a missing single-file host
  path as a root-owned dir). **Host path:** instance var **`instance_events_dir`** =
  `${instance_working_dir}/events`, **created at instance creation** (matches the existing per-instance
  *directory* precedent → correct ownership for the container user), bind-mounted → container
  `/run/kgsm`. The shim writes `events.ndjson` inside it. The watchdog watches each container instance's
  `${instance_working_dir}/events/events.ndjson` (glob `*/events/events.ndjson`; derive `<instance_name>`
  from the path).
- **Line schema (exact, Increment 1):**
  ```json
  {"type":"player_joined","id":"<str|null>","name":"<str|null>","ts":"<ISO-8601-UTC>"}
  {"type":"player_left","id":"<str|null>","name":"<str|null>","ts":"<ISO-8601-UTC>"}
  ```
- **Rotation/restart (advisor: do NOT use size comparison):** the shim **deletes + recreates**
  `events.ndjson` (a **fresh inode**) on each container start — which works precisely *because*
  `/run/kgsm` is a directory mount. The watchdog keys on **(inode, byte-offset)** and **re-reads from 0
  when the inode changes**. No truncate-in-place + shrink-detection (races when writes outrun the poll).
- **At-least-one-non-null is the SHIM's job:** a regex match that captures neither id nor name must be
  **skipped + logged**, never emitted as `{null,null}`.

### C. Pattern delivery (blueprint → container, via env — blueprint stays authority)

- Blueprint fields → env the shim reads, **base64-encoded across the env boundary** (advisor: raw regex
  through compose-YAML→shell→shim mangles `$`/quotes/`\`/`<>` — exactly what Source/Factorio patterns
  contain):
  - `KGSM_PLAYER_JOINED_REGEX_B64` ← base64(blueprint `player_joined_regex`)
  - `KGSM_PLAYER_LEFT_REGEX_B64`   ← base64(blueprint `player_left_regex`)
- Regex supports optional named groups `(?<id>...)` / `(?<name>...)`; shim emits what it captures, the
  rest `null` (subject to the at-least-one rule). **Empty/unset → that detection disabled** (honest
  unknown, no event). Patterns authored from **real** server output, never guessed.

---

## Division of labor — 3 build-time-independent repos (runtime integration verified after)

Dependency graph: `kgsm-containers ‖ kgsm ‖ kgsm-watchdog`. The only cross-repo dep is **runtime** (the
watchdog's emit only lands once kgsm bash defines the events) — not compile-time, so all 3 proceed in
parallel. kgsm-lib needs **no change** (string-based emit).

### 1. kgsm-containers
- Shared base image `kgsm-base` (`FROM steamcmd/steamcmd:debian`) with **tini** as PID 1 + the detection
  shim. Refactor all 6 images (enshrouded, vrising, empyrion, abioticfactor, theforest, lotrrtm) onto it.
- **Standardize logging:** every game's output must land in the bind-mounted in-container log file (fix
  enshrouded, which currently `exec`s to stdout with no file).
- **Shim = an ADDITIVE background tailer** of that game log file. It must NOT reroute the game's stdout
  and must NOT touch the existing per-game `manage.sh` launch / **stdin-FIFO stop+save** path (some games
  stop via a save command, not SIGTERM — that path must survive untouched). It base64-decodes the regex
  env, matches lines, writes NDJSON (schema B) to a fresh-inode `/run/kgsm/events.ndjson`, enforcing the
  at-least-one-non-null rule. tini handles PID-1 signal forwarding.
- **Consumes only:** NDJSON schema (B) + env var names (C). No dep on the other two repos.

### 2. kgsm (bash; **no kgsm-lib change**)
- Define `instance_player_joined` / `instance_player_left` in `commands/handlers/events.sh`
  (EVENT_CONFIGS param specs + any `EC_*`), following the existing 35-event pattern; make
  `kgsm events emit instance_player_joined <instance> [id] [name]` work with **nullable** id/name (decide
  empty-string vs JSON-null and keep it honest).
- Blueprint fields `player_joined_regex` / `player_left_regex`; wire them into the **`container.compose`
  `environment:`** block **base64-encoded** as `KGSM_PLAYER_JOINED_REGEX_B64` / `_LEFT_REGEX_B64`.
- New instance var `instance_events_file` (`templates/instance.tp` + `__logic_create_base_instance`) +
  the **bind-mount** in `container.compose` (host `${instance_events_file}` → `/run/kgsm/events.ndjson`).
- **Consumes only:** the frozen names in A/B/C.

### 3. kgsm-watchdog (.NET 10 AOT; the host-side ingester = its container role)
- Watch the kgsm instances dir for `*/events/events.ndjson`; tail each by **(inode, offset)**,
  re-read from 0 on inode change; parse NDJSON (schema B); emit `instance-player-joined` / `-left` (DASH
  on the wire; kgsm normalizes `-`→`_`) via
  `EmitWithProvenance(eventType, actor:"system", origin:"system", instanceName, id, name)` — derive
  `instanceName` from the path. **NEVER shell `docker`** (reads files + emits only — native charter
  intact). AOT-safe, 0 ILC warnings.
- **Accepted coupling (documented):** a container-only host must run the watchdog purely as the event
  forwarder. Accepted because the watchdog is the engine's resident base, and standing up a separate
  ingester daemon is heavier; revisit if container-only deployment becomes a real target.
- **Consumes:** NDJSON schema (B) + event strings (A). No kgsm-lib change required.

## Validation per repo (self-contained; build, don't commit)
- **kgsm-containers:** `docker build` the base + ≥1 game image if docker is available (report if not);
  shellcheck the shim.
- **kgsm:** `shellcheck` clean; `tests/run.sh unit` for the new events + emit path.
- **kgsm-watchdog:** `dotnet build` + AOT `dotnet publish -r linux-x64` (0 IL2026/IL3050/ILC); unit tests
  for NDJSON parse + inode/offset tracking + the at-least-one-non-null guard.

## Integration (after the 3 land; likely can't fully run without docker + deployed watchdog)
End-to-end: a container emits a join line → watchdog ingests → `kgsm events emit` lands a real
`instance_player_joined` on the wire. Verify the seams meet; flag what needs a live host.
