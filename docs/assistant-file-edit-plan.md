# Plan: assistant edits a game's own config file (staged `write_file`)

Status: **design locked — building.** Authority for this feature until it ships; then
this doc is retired and the behaviour is described in `docs/ARCHITECTURE.md` +
`CHANGELOG.md`. Decisions locked (see "Resolved decisions" at the end).

## The driving case

A user of a shared Palworld server asked (paraphrased): *"I tried to set up the world
settings / offline punishments but couldn't figure it out — the world config file in
the server seems empty."*

That's the **Ketchup** instance (Palworld). Verified on the host:

- Live file `…/Ketchup/install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini` is
  **1 byte — genuinely empty**. That is Palworld's *expected* starting state, not a bug.
- `…/Ketchup/install/DefaultPalWorldSettings.ini` (≈3.8 KB) holds the reference block:
  `[/Script/Pal.PalGameWorldSettings]` → `OptionSettings=(Difficulty=…,ExpRate=…,`
  `PalCaptureRate=…,DeathPenalty=…,…)`. The fix for any world setting is to copy that
  block into the empty live file and edit the one value; the game reads it at boot.

A useful reality the *search* leg surfaces: "offline punishments" is **not** a native
Palworld toggle. Part of helping is the assistant discovering what is actually
configurable (death penalty, raid/PvP options) rather than inventing a setting.

The imagined assistant flow: **search** how the setting works → **read** the instance's
config file → **propose** an edit the user accepts or denies.

## What already works vs. the gap

Grounded in the code (read-only investigation, 2026-07-19):

| Leg | Tool | State | Notes |
|---|---|---|---|
| Search how a setting works | `search` | **works** | Live on hotrod with BOTH backends — Tavily web (`WebSearch__ApiKey` set) + local RAG (`Rag__Enabled=true`). Returns cited passages with provenance. |
| Read the instance's config file | `read_file` / `list_files` | **works** | Jailed to the instance dir; already reaches `install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini` and `DefaultPalWorldSettings.ini`. 64 KB read cap. Authorized-read tier. |
| Propose the edit | — | **THE GAP** | No write/edit-file tool exists. `set_config_value` only pokes KGSM's own `.config.ini` (flat `key=value`, denylisted) — it cannot touch a game config file, and Palworld's `OptionSettings=(…)` tuple isn't even standard INI. |

Key structural findings that shape the build:

- The read jail lives **entirely in the assistant's own C# adapter**
  (`TheKrystalShip.Kgsm.Assistant.Infrastructure/Kgsm/KgsmServerOperations.cs`):
  `ResolveInstanceBoundaryAsync` (resolves the boundary *through* kgsm's `instances find`,
  drift-proof) + `IsWithin` (trailing-separator prefix check, defeats `..` and
  `inst`-vs-`inst-evil`) + a final-component symlink re-check + `IsNonRegularFile`
  (a small `stat()` interop that refuses FIFO/socket/device). **A write reuses all of
  this verbatim.**
- **kgsm-lib has no file read/list/write method and no jail at all** — every kgsm-lib
  service just shells the engine. The read path does its own C# `System.IO` within the
  jail; only the *boundary* is resolved through kgsm (`IInstanceService.FindConfigPath`).
  So a C#-side **write** within the same jail is consistent with the existing read
  precedent — it is **not** "shelling out to `kgsm.sh`". The chokepoint invariant is about
  engine *data/actions* (status, lifecycle), not raw co-located file bytes, which reads
  already touch directly. **No kgsm-lib, no kgsm engine, no kgsm-api change is needed.**
- The staging/confirm plumbing is fully reusable: append-only `ConfirmationKind` (tokens
  encode `(int)Kind`), the ambient per-turn `IConfirmationContext`, the stateless HMAC
  token (`ConfirmationTokenService`), the gate, and `ServerAssistant.ConfirmAsync`.

## The design — a generic staged `write_file`

Chosen approach (per decision): **a generic, propose-only, full-file `write_file`.** The
model reads the current file + any default/reference file + search results, composes the
**complete** new file content, and stages it. The human confirms against a preview
(a diff vs. the current file). Game-agnostic — it handles Palworld's tuple line,
`server.properties`, JSON, arbitrary text — and the confirmation + preview is the safety
net. (Rejected: a section/key INI editor can't represent Palworld's single-line
`OptionSettings=(…)` tuple; a diff/patch tool is error-prone for a 12B local model and
there's nothing to patch against an empty file.)

### 1. The tool

`LlmTools.cs` — a new `Tool WriteFile = new("write_file")` in the **StagedCommands** tier
(authorized-only, propose-only, counts against `MaxStagedCommandsPerMessage`). Parameters:

- `instance_name` (required) — same resolution as every instance tool.
- `path` (required) — file to write, relative to the server's own directory (same
  semantics as `read_file`'s `path`, e.g. `install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini`).
- `content` (required) — the **complete** new file content (this OVERWRITES the file; it
  is not a patch).

Description intent (disambiguation is load-bearing for routing): this writes a **game's
own config file** and REPLACES its whole content; it is propose-only (the user confirms
against a preview); it is distinct from `set_config_value`, which changes KGSM's own
`.config.ini` (ports, args, auto-update). Tell the user it takes effect on the next
restart and never claim to have written it yourself.

**Always stages** — like `set_config_value` and `install`, `write_file` is NOT
auto-executed even on an auto-accept turn. A whole-file overwrite is too consequential to
run without an explicit human look at the diff.

### 2. The new capability — `WriteInstanceFileAsync`

Add to the port `TheKrystalShip.Kgsm.Assistant/Ports/IServerOperations.cs`:

```csharp
Task<Result> WriteInstanceFileAsync(
    string instance, string relativePath, string content, CancellationToken cancellationToken = default);
```

Implement in `KgsmServerOperations.cs`, **reusing the read jail**:

1. `ResolveInstanceBoundaryAsync(instance)` → real boundary dir (or honest error).
2. `candidate = Path.GetFullPath(Path.Combine(realDir, relativePath))`; refuse unless
   `IsWithin(realDir, candidate)`.
3. If the file **exists**: refuse if `IsNonRegularFile` (don't clobber a socket/device);
   resolve a final-component symlink and re-check `IsWithin` (an in-dir link pointing out).
   If it **doesn't exist**: require the *parent* directory to already exist and be
   `IsWithin` the boundary (do not create arbitrary deep trees; a config dir always
   exists). Creating a new file in an existing in-jail dir is allowed (covers a
   missing-but-expected config).
4. **Backup-before-overwrite:** if the target exists and non-empty, copy it once to a
   sibling `"<file>.kgsmbak"` (in-jail) before writing — cheap reversibility ("undo my
   last edit"). Overwrite the same `.kgsmbak` each time (last-good, not a history).
5. **Atomic write:** write to a temp file in the same directory, then `File.Move(temp,
   candidate, overwrite: true)` — an atomic replace on one filesystem, so a live-reading
   server never sees a torn file. UTF-8, no BOM.
6. Size cap **`MaxWriteBytes = 10 MB`** — a generous safety bound, not a target (a 12B
   model realistically emits KBs; the headroom is for future non-config uses). Refuse
   larger with an honest error. Because content this large cannot ride the confirmation
   token, the Service persists it server-side — see the carrier section below.
7. Return `Result.Success` with a short note, or `Result.Failure` with the reason.
   The summary should remind that a running server applies it on next restart.

This mirrors `ReadInstanceFileAsync` exactly in structure; nothing new about the jail.

**Read-cap asymmetry caveat.** Reads truncate at 64 KB; a whole-file overwrite composed
from a truncated read would drop everything past 64 KB. The **diff preview** is the
backstop (a truncated overwrite shows as a huge deletion → the human denies) and
`.kgsmbak` makes it reversible. The prompt instructs the model to only propose a full
overwrite of a file it has read in full (or a new file); large existing files are for a
later read-cap bump, out of this scope.

### 3. How the file body rides confirmation (the crux fork)

The staged `PendingConfirmation` still carries the **full content in `ConfigValue`** in
memory during the turn — the `ConfigKey`=path, `ConfigValue`=content overload
(`OpenPorts` already overloads those two fields). That is all the **CLI** surface needs:
it drains the staged confirmation and confirms **in-process**, content in memory, so the
CLI works at any size with no token and no store.

The **Service** (out-of-band HTTP/SSE confirm) is where a large body can't ride the
stateless HMAC token. `ConfirmationTokenService.Create` serializes `ConfigValue` into the
signed token, and a 10 MB token is untenable. So for `WriteFile` the Service stages the
content **server-side (B2)**:

- A new Service component **`IPendingWriteStore`** (SQLite-backed — reuse the Service's
  existing conversation-history DB, add a `pending_writes` table). API:
  `string Put(string content, DateTimeOffset expiry)` → opaque `pendingId`;
  `bool TryTake(string pendingId, out string content)` — **single-use** (deletes on take);
  plus TTL sweep of expired rows. SQLite (not in-memory) so a staged write survives a
  Service restart within the token TTL, preserving the existing restart-durability
  property.
- **At token-mint time** (the Service drains `scope.Staged` after a turn): for a
  `WriteFile` confirmation, `Put` the content → `pendingId`, then mint the token from a
  confirmation whose `ConfigValue` is the **`pendingId`, not the content**. The token thus
  stays tiny; the signed `pendingId` can't be forged, and it's single-use + TTL-bound
  (replay-safe). `ConfirmationTokenService` itself is **unchanged** — it just signs
  strings; the swap happens at the call site.
- **The `command.proposed` SSE frame** carries the display payload separately from the
  token: `{ token, kind:"write_file", instance, file:{ path, proposedContent } }`.
  `proposedContent` is for the diff preview (bounded by the cap; sent once, not in the
  token). The Service can also read the *current* file via the same jail to attach a
  server-computed unified `diff`.
- **At confirm time** (`/confirm`): validate the token → a confirmation with
  `ConfigValue = pendingId`; if `Kind == WriteFile`, `TryTake(pendingId)` to rehydrate the
  real content (honest error if expired/already used), swap it back into `ConfigValue`,
  then call `ServerAssistant.ConfirmAsync`. `ServerAssistant` always sees real content and
  stays surface-agnostic (no store knowledge).

This decouples content size from the token so the 10 MB cap is real, while keeping the CLI
path trivial and `ConfirmationTokenService` / `ServerAssistant` unchanged.

### 4. Confirmation kind + metadata

`Confirmations.cs`:

- Append `WriteFile` to `ConfirmationKind` (after `OpenPorts` — never reorder; tokens
  encode `(int)Kind`).
- `ConfirmationKinds.Verb(WriteFile)` → `"write to a file on"`;
  `PastTense(WriteFile)` → `"had a file updated"`.
- Leave it **out** of the `Commands` set (it has its own confirm path, like `SetConfig`
  and `Install`). **Destructive?** — default **no** (Uninstall is the only Destructive
  kind and gets stronger type-the-name friction). A `.kgsmbak` makes it reversible and the
  diff preview is the real friction. This is a judgement call to confirm (see Open
  questions).

### 5. Dispatch, confirm, gate

- **Stage** — `ToolDispatcher`: a `StageWriteFileAsync` case (+ the `if (call.Name == …)`
  line in `ExecuteAsync`). Resolve the instance; require non-empty `path` and `content`;
  enforce `MaxWriteBytes` at stage time (so an oversized body never reaches the token);
  `_confirmations.Stage(new PendingConfirmation(WriteFile, resolved, ConfigKey: path,
  ConfigValue: content))`; return the "Staged — awaiting confirmation" string. Never
  executes inline.
- **Confirm** — `ServerAssistant.ConfirmAsync`: a `ConfirmWriteFileAsync` arm that
  re-validates the instance against live inventory, then calls
  `_operations.WriteInstanceFileAsync(match, path, content, ct)` and reports the real
  outcome.
- **Gate** — `write_file` is a staged command → already refused for unauthorized callers
  and counted against `MaxStagedCommandsPerMessage`. Confirm at the standard confidence.

### 6. Prompt guidance (`KgsmAssistantPrompts.cs`)

Add a few sentences (Preamble + ActionsAllowed) teaching the flow and the
`set_config_value` vs `write_file` split, host/game-agnostic:

> To change a setting in a game server's **own** config file (as opposed to KGSM's
> `.config.ini`), first read the file with `read_file`, and read any default/reference
> file next to it (e.g. `DefaultPalWorldSettings.ini`); use `search` to confirm what the
> setting does; then propose the change with `write_file`, providing the **complete** file
> content and preserving the settings already there. It's propose-only — tell the user
> it's awaiting their confirmation and that a running server applies it on the next
> restart. `set_config_value` is for KGSM's own settings (ports, launch arguments,
> auto-update); `write_file` is for the game's own config files.

## Safety model

- **Jailed** to the instance directory (the read jail, reused unchanged).
- **Propose-only**, authorized-only, always staged (never auto-run), counted against the
  per-message cap.
- **Diff preview** at confirm time — the human accepts/denies against the actual change,
  not a description.
- **Reversible** — a `.kgsmbak` sibling holds the previous content ("undo my last edit").
- **Atomic** replace — no torn file for a live-reading server.
- **Bounded** — 16 KB content cap; non-regular files refused; new files only in an
  existing in-jail directory.
- **No new attack surface off-host** — pure local file I/O the assistant already does for
  reads; no engine/API/lib change.

## The chat surface leg (kgsm-web) — deferred, specified

For the "accept/deny" UX to be meaningful the confirm card must **preview the change**:

- Extend the `command.proposed` SSE frame (`…Service/Contracts.cs`) with an optional
  `file` block for the `write_file` kind — `{ path, proposedContent }`, and optionally a
  server-computed unified `diff` against the current file (the Service can read the
  current file via the same jail to build it). The frame already carries per-kind optional
  fields (e.g. `instanceName` for install), so this is additive.
- A kgsm-web confirm-card variant that renders the path + a diff/preview (reuse the
  existing diff/`CodeEditor` surface) with Accept/Deny, wired to the existing command
  confirm/deny action. This is the SPA analog of the CLI's y/N.
- Bump kgsm-web version + CHANGELOG.

Build after the assistant side is verified via CLI (per the phased order below).

## Eval additions (`…Assistant.Eval`)

Routing benchmark (`BenchmarkSuite.cs`/`Checks.cs`), bump `Version`:

- A "change a Palworld world setting" case → expects a `read_file`/`search` read leg and
  a **staged `write_file`** (a `StagesWith(ConfirmationKind.WriteFile, …)` payload check
  on the path), proposed-not-executed, `ResolvedNotAsked`.
- A disambiguation pair: a KGSM setting ("turn on auto-update") → `set_config_value`; a
  game setting ("make the days longer" / "change difficulty") → `write_file`. Guards the
  two writers from routing collisions.
- Score trajectory only (harness invariant #1) — never a world fact.

## Testing & live verification

- **Unit** — `WriteInstanceFileAsync`: `..` escape refused, symlink-out refused,
  non-regular refused, size cap, atomic replace, `.kgsmbak` created, new-file-in-existing-
  dir allowed, new-file-in-missing-dir refused. Dispatcher staging (oversized refused
  pre-token). Confirm arm re-validates. Token round-trips content (mint → validate →
  identical `ConfigValue`). Gate refuses unauthorized.
- **Live (Ketchup, Palworld)** — a CLI turn: *"set the day length / difficulty on
  Ketchup"* → assistant reads the empty `PalWorldSettings.ini` + `DefaultPalWorldSettings.ini`,
  searches the setting, stages a `write_file` that populates the
  `[/Script/Pal.PalGameWorldSettings] OptionSettings=(…)` block with the one value changed.
  Confirm → assert the file now holds the block, a `.kgsmbak` exists, restart Ketchup,
  confirm the setting takes effect. Also assert a denied confirmation leaves the file
  untouched.

## Build order (three phases, one commit each)

1. **Backend feature — both surfaces work (kgsm-llm):** `WriteInstanceFileAsync` (jail
   reuse + atomic replace + `.kgsmbak` + 10 MB cap + new-file) + `write_file` tool +
   `ConfirmationKind.WriteFile` + dispatch/stage/confirm/gate + prompt + the Service
   `IPendingWriteStore` (SQLite) + token-mint/confirm rehydration + the `command.proposed`
   `file` frame block + unit tests. Version bump + CHANGELOG. Verify live on Ketchup via
   the CLI. Commit.
2. **Eval (kgsm-llm):** routing cases (staged `write_file` for a game setting) +
   `set_config_value`-vs-`write_file` disambiguation pair; bump benchmark version; live
   routing pass. Commit.
3. **kgsm-web:** a confirm-card variant that renders the `file` preview (path + diff) with
   Accept/Deny, wired to the existing command confirm/deny action. Version bump +
   CHANGELOG. Commit.

Then, once all three are in: final commit if needed, **tag** each touched repo at its new
version, and **deploy** (assistant Service + kgsm-web) — all on `main`.

Phase 1 is a complete, correct backend (CLI **and** Service work at 10 MB); phase 2
guards routing; phase 3 adds the in-chat accept/deny UX.

## File-touch checklist

kgsm-llm (phase 1 — brain):
- `TheKrystalShip.Kgsm.Assistant/LlmTools.cs` — `WriteFile` tool + definition; add to
  `StagedCommands`/`StagedCommandTools`.
- `TheKrystalShip.Kgsm.Assistant/Confirmations.cs` — `ConfirmationKind.WriteFile` +
  `Verb`/`PastTense`.
- `TheKrystalShip.Kgsm.Assistant/ToolDispatcher.cs` — `StageWriteFileAsync` + dispatch line.
- `TheKrystalShip.Kgsm.Assistant/ServerAssistant.cs` — `ConfirmWriteFileAsync` arm in the
  `ConfirmAsync` switch.
- `TheKrystalShip.Kgsm.Assistant/Ports/IServerOperations.cs` — `WriteInstanceFileAsync`.
- `TheKrystalShip.Kgsm.Assistant.Infrastructure/Kgsm/KgsmServerOperations.cs` — impl
  (reuse the jail helpers).
- `TheKrystalShip.Kgsm.Assistant/KgsmAssistantPrompts.cs` — guidance.

kgsm-llm (phase 1 — Service):
- `TheKrystalShip.Kgsm.Assistant.Service` — a new `IPendingWriteStore` + SQLite impl
  (reuse the conversation-history DB) with `Put`/`TryTake` + TTL sweep.
- The turn/drain path that mints tokens — for `WriteFile`, `Put` content → swap
  `ConfigValue` to the `pendingId` before minting; the `/confirm` handler — for
  `WriteFile`, `TryTake` to rehydrate content before `ConfirmAsync`.
- `…Service/Contracts.cs` + `SseTurnWriter` — the `command.proposed` `file` block
  (`path`, `proposedContent`, optional `diff`).
- `ConfirmationTokenService` needs **no change** (it just signs the swapped strings).

Cross-cutting: tests across the above; `CHANGELOG.md` + `…Assistant.csproj` `<Version>`.

## Resolved decisions (locked)

1. **Not `Destructive`.** Rely on the diff preview + `.kgsmbak` for friction (no
   type-the-name gate). `write_file` stays out of the `Destructive` set.
2. **10 MB cap** (not 16 KB) — generous headroom for future non-config uses. This is what
   forces server-side staging (B2) for the Service token path; the CLI is unaffected.
3. **Keep `.kgsmbak`** — a last-good sibling backup for easy "undo my last edit".
4. **Allow new-file creation** inside an existing in-jail directory (not overwrite-only).
