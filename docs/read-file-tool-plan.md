# Plan: replace `view_config_file` with generic `read_file` + add `list_files`

Status: PROPOSED (green-lit by owner 2026-06-23). Not yet implemented.

## Goal

Give the assistant broad, familiar file access — read any text file inside a
server instance's directory, and discover what files exist — replacing the
single-purpose `view_config_file` tool. Stays inside the existing instance-dir
jail. No freeform `edit_file` (explicitly out of scope — it would end-run
`set_config_value`'s kgsm key denylist and break the kgsm-owns-config invariant).

## Decisions locked (owner)

1. **Replace** `view_config_file` outright — do not keep it alongside (fewer,
   non-overlapping tools is the small-model design principle; `read_file`
   subsumes it).
2. **Separate `list_files` tool** for discovery (not folded into `read_file`).
3. **Truncate-with-note** on oversize files (already the existing behavior).
4. **No secret redaction** — game-server configs, trusted operators; the
   `key=value` redaction pass is dropped. Conscious downgrade: raw file contents
   (incl. any tokens/passwords) now flow into model context and conversation
   logs.
5. Reuse the existing instance-dir anchor — "good enough" for the use case.

## What exists today (verified)

- `view_config_file` (tool `view_config_file`): inline read of
  `<instance>.config.ini`, secrets redacted. In the **AuthorizedReadOnly** tier
  (gated to action-authorized callers; exposes file contents). Handler:
  `ToolDispatcher.ViewConfigFileAsync` → `IServerOperations.ReadInstanceFileAsync`.
- `set_config_value`: propose-only, single `key=value`, kgsm-validated. **Unchanged by this plan.**
- The read jail (`KgsmServerOperations.ReadInstanceFileAsync`) is already robust:
  resolves the boundary **through kgsm** (`FindConfigPath` → `__find_instance_config`),
  canonicalizes the boundary symlink (`ResolveLinkTarget(returnFinalTarget:true)`),
  defeats `..` via `Path.GetFullPath`, confines with `IsWithin`, re-checks the
  final-component symlink, and reads through `ReadCappedTextAsync`
  (`MaxFileBytes = 64 KB`, appends `… (truncated)`).
- Real instance layout (verified on host) — the config's directory **is** the
  instance root, and game files are subdirs under it, so the existing jail
  already reaches them:
  ```
  …/instances/factorio/factorio-test/
  ├── factorio-test.config.ini      ← view_config_file reads this
  ├── factorio-test.log             (73 KB, grows)
  ├── .factorio-test.sock           ← FIFO — reading BLOCKS forever (footgun)
  ├── install/ logs/ saves/ backups/ temp/
  ```
- The bot (`MediatorServerOperations`) **stubs** `ReadInstanceFileAsync` with a
  "not available on the Discord surface yet" failure — so new port methods cost
  it ~3 lines, not a real impl.

## Design

### Tool: `read_file` (replaces `view_config_file`; AuthorizedReadOnly tier)
- Params: `instance_name` (required); `path` (optional) — "Relative path inside
  the server's directory, e.g. `server.properties` or `logs/latest.log`. Omit to
  read the main `.config.ini`."
- Behavior: resolve instance (existing `ResolveInstanceAsync`); if `path`
  blank/omitted → default to `<resolved>.config.ini` (preserves the exact
  "show me X's config" affordance, critical for the 12B model which can't guess
  filenames); call `ReadInstanceFileAsync(resolved, path)`; return content
  verbatim (no redaction).

### Tool: `list_files` (new; AuthorizedReadOnly tier)
- Params: `instance_name` (required); `subdir` (optional) — "Subdirectory to
  list, e.g. `logs` or `install`. Omit for the server's top-level directory."
- Behavior: resolve instance; call new port method `ListInstanceDirectoryAsync`;
  format a small text listing for the model (dirs first, then files w/ sizes).

### New port method (the only cross-repo cost)
`IServerOperations.ListInstanceDirectoryAsync(string instance, string? relativeSubdir = null, CancellationToken)`
→ `Task<Result<IReadOnlyList<InstanceDirEntry>>>`, new record
`InstanceDirEntry(string Name, bool IsDirectory, long Size)`.
Infra impl: same boundary resolution + `IsWithin` jail as the read path; require
the target be a directory; enumerate ONE level (non-recursive);
cap entry count (~200) to bound listing size; sort dirs-first.

### New read guards (infra, inside the existing read path — no signature change)
The 64 KB cap + truncation note already exist. Add:
1. **Non-regular-file guard (required — the FIFO).** Before opening, `stat` the
   resolved file and refuse anything that isn't a regular file (FIFO/socket/
   device/dir) with a clear message, so a read of `.factorio-test.sock` returns
   "not a readable file" instead of hanging forever. `System.IO` doesn't expose
   st_mode type bits → small `LibraryImport` P/Invoke to `stat` + `S_ISREG`
   (Linux-only, which the whole stack already is). Belt-and-suspenders: wrap the
   open+read in a short timeout so no path can wedge the tool.
2. **Binary guard (should).** If the first chunk contains a NUL byte, return a
   short "binary file, N bytes, not shown" note instead of dumping bytes into
   context.
3. Improve the truncation note to include the byte size (minor).

## Files to change (blast radius)

**kgsm-llm only — `read_file` + guards:**
- `TheKrystalShip.Kgsm.Assistant/LlmTools.cs` — remove `ViewConfigFile`; add
  `ReadFile` (`read_file`) + `ListFiles` (`list_files`) with params; both into
  the `AuthorizedReadOnly` array.
- `TheKrystalShip.Kgsm.Assistant/ToolDispatcher.cs` — replace the
  `ViewConfigFile` case + `ViewConfigFileAsync` with `ReadFileAsync` (optional-
  path default → config) and add `ListFilesAsync`; **delete `RedactSecrets` +
  `SecretKeyHints`** (redaction dropped).
- `TheKrystalShip.Kgsm.Assistant/ServerAssistant.cs` — generalize the
  authorized-read refusal text ("view server configuration" → "read server
  files"). Gate logic unchanged (both tools are AuthorizedReadOnly → already
  covered by `IsAuthorizedRead`).
- `…Infrastructure/Kgsm/KgsmServerOperations.cs` — add the non-regular/binary
  guards to the read path; implement `ListInstanceDirectoryAsync`.

**Cross-repo — the new port method (`list_files`):**
- `…/Ports/IServerOperations.cs` — declare `ListInstanceDirectoryAsync` + the
  `InstanceDirEntry` record.
- `kgsm-bot/src/KGSM.Bot.Discord/Llm/MediatorServerOperations.cs` — 3-line stub
  (mirror the existing `ReadInstanceFileAsync` "not available on Discord" stub).
- `TheKrystalShip.Kgsm.Assistant.Eval/Fixtures.cs` — fake impl of the new method.
- Any test fakes of `IServerOperations` (grep `: IServerOperations` + test
  doubles in `…Assistant.Tests`).

**Tests:**
- `…Assistant.Tests/ToolDispatcherTests.cs` — view_config_file cases → read_file
  (incl. optional-path default) + new list_files cases; drop redaction asserts.
- `…Assistant.Tests/ServerAssistantTests.cs` — tool-set/gate refs to
  `ViewConfigFile` → `ReadFile`/`ListFiles`.
- `…Infrastructure.Tests/KgsmServerOperationsTests.cs` — add cases: non-regular
  file (FIFO) refused, binary handled, listing within jail, `..`/outside refused.
- `…Service.Tests/ConfigEditLiveTests.cs` — `ViewConfigPrompt_CallsViewConfigFile`
  → assert routing to `LlmTools.ReadFile`; the direct-port read test stays valid
  (port unchanged), rename for clarity.

**Docs / eval corpus (don't leave stale tool names that the eval will grade against):**
- `…Eval/mcq/corpus/gemma-assistant-eval.md`, `…/assistant-toolbox-plan.md`,
  `…Eval/README.md`, `docs/wire-contract.md`, `kgsm-llm.md` — update
  `view_config_file` references to `read_file`/`list_files`.

## Non-goals / out of scope
- `edit_file` (any freeform write). Config edits stay on `set_config_value`.
- Recursive listing; per-file redaction; reading outside the instance jail.
- No kgsm or kgsm-lib change (infra reads the filesystem directly within the
  kgsm-resolved boundary, as the read path already does).

## Ordered checklist
1. Port: add `ListInstanceDirectoryAsync` + `InstanceDirEntry` to `IServerOperations`.
2. Infra: implement listing; add non-regular + binary guards to the read path.
3. Bot + Eval + test fakes: implement the new port method (stub on the bot).
4. `LlmTools`: swap `view_config_file` → `read_file` + `list_files`.
5. `ToolDispatcher`: `ReadFileAsync` (path default) + `ListFilesAsync`; drop redaction.
6. `ServerAssistant`: generalize refusal text.
7. Tests: update + add (FIFO refusal is the must-have new case).
8. Prompt/docs/eval corpus: rename references.
9. `dotnet build` + `dotnet test` the kgsm-llm solution; **build kgsm-bot**
   (port method) to confirm no break. Live smoke: `read_file` a log + the
   default config, `list_files` the instance root, and confirm `.sock` is
   refused (not hung).

## Outcome — BUILT 2026-06-23 (uncommitted)

Implemented exactly as planned. The eval-corpus rename (step 8) was scoped DOWN
after inspection: the graded answer key (`mcq/questions.json`) has no
`view_config_file` reference, and the `mcq/corpus/*.md` files are historical
(a past eval-findings report + a dated "locked 2026-06-12" design snapshot), so
rewriting them would falsify the record — left intact; only the live `m7` spec
was updated.

- **kgsm-llm**: full suite green **509/509**. New unit tests: read_file
  default→config / explicit-path, list_files formatting + subdir passthrough,
  and (infra) FIFO-refusal via a real `mkfifo`, binary-not-dumped, listing,
  `..`-escape. New deterministic live test lists the REAL `factorio-test` dir and
  refuses its real `.factorio-test.sock` FIFO ("regular file") — proven green.
- **kgsm-bot**: builds clean; LLM-wiring tests **27/27** (3-line port stub).
- The non-regular guard uses a `DllImport stat` + `S_ISREG` check (System.IO
  can't see st_mode type bits); JIT project, so no `/unsafe` / AOT concern.
