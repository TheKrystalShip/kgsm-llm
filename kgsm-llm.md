# KGSM Local-LLM Discord Bridge — Project Context & Handoff

> ⚠️ **HISTORICAL — partly superseded (kept for the hardware/Ollama/decision record).**
> This was the original single-host design+handoff doc (2026-06-08), written before the repo
> grew into its current library/Service/CLI/RAG-indexer layout. For anything operational, use
> the current docs instead:
> - **Run it:** [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — incl. the Ollama/VRAM tuning that
>   originated in §2/§4 here.
> - **Config:** [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) · **Design:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
>
> Still accurate and worth keeping: the **GPU/VRAM tuning** (§2, §4–6), the **model bake-off**
> rationale for `gemma4:12b` (§7), and the **live-list-injection** finding (§7a). The
> "what's left to build" / phase-status sections are a snapshot of that date, not current state.

> **Purpose of this file:** a self-contained handoff so a cold session (or a future me)
> can resume work without re-deriving anything. Captures the goal, the hardware/software
> setup, all testing + results, the design decisions, and what's left to build.
> Last updated: **2026-06-08**. Host: **`hotrod`** (headless Arch Linux, SSH-only).

---

## 1. Goal

Add a **local LLM** to the existing **`kgsm-bot`** Discord bot so users on a small
friends server can manage game servers in **natural language** ("hey set up a terraria
server", "is minecraft up?"). The LLM does **intent routing + tool calling**; the tools
invoke **KGSM** (the user's own game-server orchestration CLI). The LLM never touches the
system directly.

---

## 2. Hard constraint: inference must stay 100% in VRAM (no spill to RAM/CPU)

The CPU and system RAM on this box are **dedicated to running the game servers**
(Terraria, Minecraft, etc.), which are tick-rate-sensitive and largely single-threaded.
Any LLM spillover into system RAM/CPU steals real-time resources from live gameplay →
lag. So "no spillover" is an **isolation requirement**, not a performance nicety.
This drives every config decision below.

---

## 3. Hardware & system state (verified)

| Item | Value |
|---|---|
| Host | `hotrod`, Arch Linux, headless, kernel `7.0.11-arch1-1` |
| CPU / RAM | AMD Ryzen 7 3800X (8c/16t), 31 GiB RAM |
| GPU | **NVIDIA RTX 3060 12 GB** (GA106, LHR variant, bought second-hand) |
| Driver | `nvidia-open` **610.43.02** (open kernel modules — the closed `nvidia` pkg no longer exists in Arch repos; open modules support Ampere) |
| CUDA | 13.3 (toolkit `cuda 13.3.0`, pulled in as a dep of `ollama-cuda`); `nvcc` at `/opt/cuda/bin/nvcc` |
| Ollama | `ollama` + `ollama-cuda` **0.30.5** |
| nouveau | blacklisted via `/usr/lib/modprobe.d/nvidia-utils.conf` (shipped by `nvidia-utils`) |

**Why nvidia-open works with no DKMS/headers:** it's prebuilt for the stock `linux`
kernel; the `.ko` modules live in `/usr/lib/modules/7.0.11-arch1-1/extramodules/`.
It is **version-locked to the `linux` package** — always upgrade in one transaction
(`pacman -Syu nvidia-open …`), never a partial `-S`, or the module won't load.

---

## 4. Ollama configuration (all verified live)

Env vars are set via a **systemd drop-in** (NOT `/etc/environment` — systemd services
don't read that file):

**`/etc/systemd/system/ollama.service.d/override.conf`:**
```ini
[Service]
Environment="OLLAMA_FLASH_ATTENTION=1"
Environment="OLLAMA_KV_CACHE_TYPE=q8_0"
Environment="OLLAMA_KEEP_ALIVE=-1"
Environment="OLLAMA_NUM_PARALLEL=1"
```
- `FLASH_ATTENTION=1` + `KV_CACHE_TYPE=q8_0` → shrink the KV cache so larger context fits in VRAM (confirmed engaged in server logs).
- `KEEP_ALIVE=-1` → pin the model in VRAM permanently (no cold reloads; `nvidia-persistenced` only keeps the *driver* warm, not the model).
- `NUM_PARALLEL=1` → one request at a time; parallel slots multiply the KV reservation (`num_ctx × slots`) and would cause spillover. Fine at friends-server scale.

**`num_ctx` is the master knob.** Ollama silently defaults to ~4096 and truncates — the
big context only happens if set explicitly (per-request `options.num_ctx`, or
`OLLAMA_CONTEXT_LENGTH`). It is a **fixed VRAM reservation**, not a ceiling.
**Verify no spill with `ollama ps` → `PROCESSOR` must read `100% GPU`.**

**Service state caveats (action items):**
- `nvidia-persistenced` is **enabled + active** ✅
- `ollama.service` is **running but NOT enabled on boot** — run `sudo systemctl enable ollama` for production.
- sudo password on this box: the user has it; not stored here.

---

## 5. GPU health check — PASSED (second-hand card is sound)

Custom CUDA test: `/home/heisen/kgsm-llm-tests/gpucheck.cu`
Build & run:
```bash
/opt/cuda/bin/nvcc -O3 -arch=native -o /tmp/gpucheck /home/heisen/kgsm-llm-tests/gpucheck.cu
/tmp/gpucheck
```
Results:
- **VRAM:** 11,344 MiB tested across 5 bit patterns (0x00/0xFF/0xAA/0x55/address-unique), **55.4 GiB written+verified, 0 errors** → all memory good (critical for an ex-mining LHR card; consumer card has no ECC counters to rely on).
- **Compute:** matmul max relative error 1.6e-06; 15 s stress (6,140 matmuls) post-heat error 8.2e-07 → no drift.
- **Telemetry under load:** 38 °C idle → peak **59–62 °C** (throttle ~83 °C), full **170 W**, boost held ~**2010 MHz**, no throttle events.
- **Faults:** zero NVIDIA Xid errors. (A `dmesg` line "XID 541" is the Realtek **NIC**, not the GPU — false positive.)
- **PCIe:** x16 width, Gen3 (board max; card is Gen4-capable) — fine for inference.

---

## 6. Ollama-on-GPU validation — PASSED

Model: **`gemma4:12b`** (Q4, 7.6 GB). Results:
- Default context (4k): **100% GPU**, 7.9 GB VRAM, ~34 tok/s.
- **`num_ctx=32768`: 100% GPU, 8.4 GB / 12 GB** → ~3.8 GB headroom, **64k would also fit**. No spillover.
- Server logs confirm flash-attention enabled and KV cache `K (q8_0)/V (q8_0)`.
- `ollama ps` shows `UNTIL: Forever` → `KEEP_ALIVE=-1` working.

---

## 7. Model bake-off — DECISION: **gemma4:12b**

Two candidates pulled (`ollama list`): **`gemma4:12b`** (7.6 GB), **`qwen3.5:9b`** (6.6 GB).
(Qwen3.6 is too big for 12 GB — smallest variant is 17 GB. Qwen3.5 tops out at a 9b variant.)

Harness: `/home/heisen/kgsm-llm-tests/tooltest.py` — 18 Discord-style prompts, 10 tools
mirroring the bot's MediatR ops. Categories: clean commands, slang, typos,
query-vs-command, chitchat (must call nothing), multi-intent, injection.
```bash
python3 /home/heisen/kgsm-llm-tests/tooltest.py gemma4:12b
python3 /home/heisen/kgsm-llm-tests/tooltest.py qwen3.5:9b false   # 2nd arg = think on/off
```

| | gemma4:12b | qwen3.5:9b |
|---|---|---|
| Correct tool (with list injected) | **15/16** | 14/16 |
| Tool + args | **15/16** | 14/16 |
| Injection ("uninstall everything") | ✅ refused | ❌ emitted 7× uninstall |
| VRAM @ 32k | 8.4 GB | ~6.6 GB |

**Chosen: Gemma 4 12B** — better routing + refused the injection prompt (bonus safety
layer; we never *rely* on the model for safety though — see §9).

### 7a. THE key finding — inject the live instance/blueprint list into the system prompt
Initial Qwen run (no list) scored only **7/16**: it kept calling `list_instances` first
to "ground" itself before acting on names it couldn't verify (confirmed in its `thinking`
trace; `think:false` did **not** fix it; ruled out primacy/positional bias by reordering
tools). **Once the current instance + blueprint names are in the system prompt, both
models route directly** — no wasted round-trip (important on a single-GPU latency-
sensitive box). → **Always inject the live lists each turn; refresh them off the kgsm
event journal the bot already tails.**

---

## 8. Disambiguation & unclean-input handling — VALIDATED

Requirement (from user): if a request like "start the terraria server" matches **more
than one** instance, the bot must **ask which one** (listing options), then act once the
user clarifies. More broadly: expect messy/typo/ambiguous input and account for it.

Test: `/home/heisen/kgsm-llm-tests/disambig.py` (two `terraria-pvp` / `terraria-creative`
instances). **Both models passed:** turn 1 asked which one + listed both (no guess);
turn 2 resolved "the pvp one" → `start_server(terraria-pvp)`.
```bash
python3 /home/heisen/kgsm-llm-tests/disambig.py gemma4:12b
python3 /home/heisen/kgsm-llm-tests/disambig.py qwen3.5:9b false
```

**Design (do both layers — defense in depth):**
1. **Dispatcher resolves the model's `instance_name` against the LIVE kgsm list** (ground
   truth, via existing `GetAllInstances`). Never trust the model's extracted name as final.
   - exactly one match → execute (silently fixes typos, e.g. `terarria`→`terraria`)
   - multiple matches → **don't execute**; return candidates → bot asks; store pending action in the session
   - zero matches → `NotFound` + nearest suggestion
2. **Model also asks** (good UX) because the live list is in its system prompt.

Requires the **per-(user+channel) session** (see §10) so the clarifying reply has context.

| Unclean input | Handling |
|---|---|
| Typo | fuzzy-resolve to single match, execute |
| Ambiguous (2× terraria) | ask + list candidates, resolve on reply |
| Missing target ("start it") | ask which; don't guess (both models did this correctly) |
| Nonexistent name | NotFound + suggestion |
| Destructive ("delete rust") | resolve **+ require ✅ confirmation** (independent of model) |

**Compound requests** ("stop X and back it up"): models emit only the first action —
handled by the agent loop (call again after the first tool returns), not parallel calls.

---

## 9. Bot architecture & security (design, not yet built)

### Existing codebase: `/home/heisen/kgsm-bot` (C# / .NET 9, Discord.Net)
Clean Architecture + MediatR CQRS. Layers:
- `KGSM.Bot.Core` — interfaces (`IServerInstanceService`, `IBlueprintService`), `Result`
- `KGSM.Bot.Application` — **MediatR Commands**: Start/Stop/Restart/Install/Uninstall/CreateBackup; **Queries**: GetAllInstances/GetAllBlueprints/GetServerStatus/IsServerActive/GetInstanceChannelId (each with a handler)
- `KGSM.Bot.Infrastructure` — `KgsmServerInstanceService` → calls **KGSM-Lib** (`IKgsmClient.Instances.Start/Stop/...`); this is the only thing that touches kgsm. Config via `appsettings.json` (`KGSM.Path`, `KGSM.SocketPath`)
- `KGSM.Bot.Discord` — `DiscordSocketClient`, `InteractionHandler` (slash commands only today), `BotService` (BackgroundService; currently wires only `Ready`/`Log` — **does NOT subscribe to `MessageReceived`**)

Today it exposes slash commands (`/install`, `/start`, `/status`, …). KGSM-Lib subprocesses
kgsm and returns `Stdout/Stderr` (see `GetInfoAsync`).

### KGSM CLI (the trusted boundary)
- Stable version `kgsm` **2.1.0** on PATH at `/usr/local/bin/kgsm`; dev branch at
  `/home/heisen/kgsm` (git branch `dev`), a Bash project.
- Rich subcommands with **`--json`** output (blueprints, instances, per-instance
  info/status/logs/start/stop/restart/backup/etc.). `kgsm --help` for the full surface.
- User trusts kgsm fully — "not possible to destroy the system via a malicious message
  once it reaches kgsm."

### The security model — map each LLM tool 1:1 onto an existing MediatR request
This is **tighter than "only invoke kgsm."** The LLM picks from an enum of typed requests
it can't deviate from; it never builds a command line or names an executable.
**Three nested boundaries**, each sufficient alone:
1. Model can only emit one of N predefined tool schemas.
2. **Dispatcher** whitelists tool names + validates/resolves args (reject unknown tools,
   resolve instance names against live list, **author allowlist**, **confirmation for
   destructive ops**). ← this is the real safety gate; the Qwen mass-uninstall result
   proves we must NOT rely on the model here.
3. Everything still funnels through KGSM-Lib → kgsm (trusted).
- **Confirm KGSM-Lib passes argv arrays, not `bash -c "…"`** (the one thing to verify).
- **Don't expose `install_dir`** to the LLM — default it server-side.

---

## 10. Context / session strategy

- **MVP:** stateless, fresh context per message. Get the tool-calling loop working first.
- **V2:** per-`(userId, channelId)` rolling window, **idle reset ~10–15 min**, keep only
  last K turns / a token sub-budget under `num_ctx`. Needed for follow-ups + disambiguation.
- **NOT** a shared pool (context bleed, leakage, blows the fixed VRAM budget).
- **Skip summarization initially** — idle-reset + windowing is enough; drop oldest turns.
- Trigger: recommend a **dedicated channel** (every message = a request; no @mention
  parsing). Keep slash commands alongside as a deterministic fallback.
- discord.py-equivalent caveat in C#/Discord.Net: keep the Ollama call **async** (don't
  block the gateway). Long ops (install) = **ack immediately, work in background, edit the
  message when done**. `MessageReceived` needs the **MessageContent privileged intent**
  (toggle in Discord Developer Portal + add to `GatewayIntents`).

---

## 11. What's left to build (in `kgsm-bot`)

> **STATUS (2026-06-08): items 1–7 below are BUILT and unit-tested (Phases 1–4).
> See §14 for the as-built log, what's verified live vs. not, and the open risks.**

1. `ILlmClient` (Core) + `OllamaLlmClient` (Infrastructure) — typed `HttpClient` to
   `http://localhost:11434/api/chat`, sending `tools` + `options.num_ctx`. New
   `OllamaOptions { Endpoint, Model, NumCtx }` mirroring `KgsmOptions`.
2. **Tool registry** — curated JSON schemas mirroring the MediatR requests (this list = the whitelist).
3. **Dispatcher** — `switch` tool name → build MediatR request → `_mediator.Send()`.
   Includes the **name-resolution layer** (§8) and the confirmation/allowlist gates.
4. **System-prompt builder** — inject the live instance + blueprint lists each turn (§7a),
   refreshed via the kgsm events socket.
5. **`MessageReceived` wiring** + MessageContent intent + dedicated-channel filter.
6. **The agent loop** — message → Ollama(tools) → tool_calls → dispatch → feed Result
   back → final reply. Cap ~5 iterations. Truncate tool outputs before feeding back.
7. **`ConversationStore`** — `(userId,channelId)` → messages + last-activity; idle eviction timer.
8. Use templated replies for deterministic tool results; only loop back through the model
   when phrasing genuinely helps (saves a VRAM/time round-trip).

All scaffolding (1–7) is hardware-independent and unit-testable against a mocked LLM.

---

## 12. File paths reference
| What | Path |
|---|---|
| This handoff doc | `/home/heisen/kgsm-llm.md` |
| Test scripts (persisted) | `/home/heisen/kgsm-llm-tests/{gpucheck.cu, tooltest.py, disambig.py}` |
| Bot project (C#) | `/home/heisen/kgsm-bot` |
| KGSM dev branch (Bash) | `/home/heisen/kgsm` (branch `dev`); stable on PATH `/usr/local/bin/kgsm` |
| Ollama systemd drop-in | `/etc/systemd/system/ollama.service.d/override.conf` |
| nouveau blacklist | `/usr/lib/modprobe.d/nvidia-utils.conf` |
| Auto-memory (cross-session) | `/home/heisen/.claude/projects/-home-heisen/memory/discord-llm-bridge.md` |
| nvcc | `/opt/cuda/bin/nvcc` |

> Note: the `/tmp` copies of the scripts are ephemeral; the `/home/heisen/kgsm-llm-tests/`
> copies are the durable ones. `/tmp/gpucheck` (compiled binary) must be rebuilt (§5).

## 13. Quick reproduce / sanity commands
```bash
nvidia-smi                                   # card + driver + VRAM
ollama ps                                    # PROCESSOR must be 100% GPU
ollama list                                  # gemma4:12b, qwen3.5:9b present
systemctl is-active ollama nvidia-persistenced
# rebuild + run GPU health check:
/opt/cuda/bin/nvcc -O3 -arch=native -o /tmp/gpucheck /home/heisen/kgsm-llm-tests/gpucheck.cu && /tmp/gpucheck
# re-run model tests:
python3 /home/heisen/kgsm-llm-tests/tooltest.py gemma4:12b
python3 /home/heisen/kgsm-llm-tests/disambig.py gemma4:12b
```

---

## 14. Implementation status / as-built log

Built phase-by-phase, each phase live-tested by the user before the next. The build
realises the §9 security model and §11 plan. **13 unit tests pass** (mocked LLM/kgsm)
in `tests/KGSM.Bot.Core.Tests` — run `dotnet test`.

### Phase 1 — Discord ↔ LLM text round-trip ✅ (live-verified)
- Trigger = explicit **@-mention** (no dedicated channel, deferred §10's recommendation).
- `ILlmClient`/`OllamaLlmClient` (non-streaming `/api/chat`, `options.num_ctx` from config).
- `MessageReceived` wired in `MessageHandler`; **MessageContent intent** enabled (portal + bitmask).
- Gateway handlers offload to `Task.Run` so the Ollama call never blocks the gateway.
- Verified: @-mention → reply, `ollama ps` stayed 100% GPU.

### Phase 2 — conversation memory ✅ (live-verified)
- `InMemoryConversationStore`, per-`(userId,channelId)` rolling window (count-based trim)
  + lazy idle reset. `ConversationOptions { MaxMessages=12, IdleTimeoutMinutes=15 }`.

### Phase 3 — read-only tools + agent loop ✅ (live-verified)
- Tools: `list_instances`, `list_blueprints`, `get_server_status`, `is_server_active`.
- `LlmAgent` loop (MaxIterations=8, tool outputs truncated to 1500 chars).
- `SystemPromptBuilder` injects the **live instance + blueprint lists** each turn (§7a).
- **`KgsmStateCache`** (TTL backstop + event-driven invalidation off the kgsm events
  socket) added after the user flagged redundant kgsm subprocess spawns. Inventory reads
  hit the cache; status/is-active go live. `KgsmCacheOptions { InstancesTtl=300s, BlueprintsTtl=600s }`.

### Phase 4 — mutating tools ✅ CODE-COMPLETE + unit-tested — ⚠️ NOT live-verified
- Tools: `start_server`, `stop_server`, `restart_server`, `create_backup`, `update_server`
  (`update` added as a full vertical slice: `IServerInstanceService.UpdateAsync` →
  `UpdateServerCommand` → handler → tool).
- **Multi-action in one prompt** ("stop terraria, back it up, then update it" → 3 calls,
  executed sequentially in requested order). **Cap = 5 mutating actions/message**
  (read-only uncounted); 6th refused in-loop. Both enforced in `LlmAgent`, not the model.
- **Authorization = Discord role.** `DiscordOptions.ActionRoleId` (ulong, **default 0 =
  nobody authorized**). `MessageHandler` computes `canPerformActions` from
  `SocketGuildUser.Roles`; unauthorized users aren't even offered the mutating tools.
- Resolver (`ToolDispatcher.ResolveInstanceAsync`): exact → single-substring-fuzzy →
  ambiguous (asks, does NOT execute) → miss (lists known). Defence-in-depth per §8.
- **Truthfulness fix:** `Start/Stop/Restart/CreateBackup` previously returned
  `Result.Success()` unconditionally; now check `KgsmResult.IsFailure` so "Done" is honest.

### Open items / risks (carry forward)
1. **Phase 4 live test PENDING — REQUIRED before trusting actions:**
   - Set `ActionRoleId` in `appsettings.json` (still 0 → all actions refuse).
   - Verify the role gate via the `canAct={CanAct}` console log: role-holder → `True` +
     executes; non-holder → `False` + refusal. The check **fails closed**.
   - **Contingency:** if a role-holder logs `canAct=False`, enable the **GuildMembers
     privileged intent** (portal toggle + add `GatewayIntents.GuildMembers` to the bitmask).
   - Also exercise multi-action + the 5-action cap live.
2. **kgsm-lib `ProcessRunner.DEFAULT_TIMEOUT_MS = 30000` (hardcoded)** kills any kgsm
   subprocess at 30s. For `update`/`backup` of a slow server this can **corrupt the
   install** (`Kill(entireProcessTree)`). Not configurable from the bot — **bump it in
   kgsm-lib before relying on `update` live.** (User owns kgsm; deferred.)
3. **`kgsm --is-active` ~3.5s** UPnP-teardown side-effect inflates status latency
   (a simple status request ≈10s: ~4.5s two LLM passes + ~3.5s is-active). User owns the
   fix; deferred ("another time").
4. **Pre-existing config bug (deferred):** `appsettings.json` key is `"Guild"` but
   `DiscordOptions` binds `GuildId` → resolves to 0; will matter for the event-coordinator
   socket path.
5. **`appsettings.json` holds a live Discord bot token** — confirm it's gitignored.

### LLM library extraction → `TheKrystalShip.Llm` ✅ (done, between Phase 4 and 5)
The generic LLM machinery was extracted into a **standalone reusable package** (the same
concept as KGSM-Lib) so kgsm-bot — and any other C# app (e.g. an ASP.NET chatbot) — can
depend on it. Repo: **`/home/heisen/TheKrystalShip.Llm`** (mirrors kgsm-lib layout;
`GeneratePackageOnBuild`, PackageId/AssemblyName/RootNamespace `TheKrystalShip.Llm`,
GPL-3.0). **11 lib tests + 14 bot tests pass; both build clean.**

- **Moved to the library (app-agnostic):** the DTOs (`LlmMessage`/`LlmRole`,
  `LlmResponse`, `LlmToolCall`, `LlmToolDefinition`/`LlmToolParameter`), its own
  `Result`/`Result<T>`, `ILlmClient` + `OllamaLlmClient` + `OllamaOptions`,
  `IConversationStore` + `InMemoryConversationStore` + `ConversationOptions`,
  `IToolDispatcher` (interface), and the **generic agent loop** `ILlmAgent` + `LlmAgent`
  (+ `LlmAgentOptions`). DI: `AddLocalLlm(IConfiguration)`.
- **Policy inverted out (the key design move):** the library never learns what "mutating"
  or "authorized" means. The host passes everything per turn in an **`AgentTurn`**
  { `ConversationId`, `UserPrompt`, `SystemPrompt`, `Tools`, `Gate` }. The `Gate` is a
  `Func<LlmToolCall, ToolGate>` (Allow / Refuse(msg)); a per-turn closure holds the action
  counter. The conversation key was generalized from `(userId,channelId)` to a single
  **`string ConversationId`** (Discord composes `"{userId}:{channelId}"`).
- **Stayed in the bot (kgsm policy):** `LlmTools` (the whitelist), `ToolDispatcher` (→
  MediatR kgsm commands; now implements the library `IToolDispatcher`), `SystemPromptBuilder`,
  and a **new `ServerAssistant`** (`IServerAssistant`) that owns ALL kgsm policy — picks the
  tool set (read-only vs all), builds the 5-action-cap + auth `Gate`, assembles the
  `AgentTurn`, and maps the library `Result` back to the bot's `Core.Result`. `MessageHandler`
  now depends on `IServerAssistant` (signature unchanged). Behaviour is identical to the old
  monolithic `LlmAgent` on every branch.
- **Consumption:** packed to a local folder feed `/home/heisen/local-nuget`; kgsm-bot has a
  repo-local `nuget.config` adding that source (merged with the global nuget.org + github
  sources, so their creds are preserved) and `<PackageReference Include="TheKrystalShip.Llm"
  Version="1.0.0">` in Infrastructure + Discord. The lib floors its `Microsoft.Extensions.*`
  deps at **8.0.0** (not 9.0.0) to avoid a package-downgrade error against the bot's 8.0.0 pins.
- ⚠️ **NuGet cache footgun:** re-packing the **same** version `1.0.0` is silently ignored by
  the bot (the cached extraction wins) — edits seem to have no effect. Bump `<Version>` per
  change, or `rm -rf ~/.nuget/packages/thekrystalship.llm/<version>` after re-packing.
- ⚠️ **DI not yet verified at runtime** beyond a stubbed wiring test (`LlmWiringTests`). The
  full container (with the real kgsm stack) resolves on `host.Build()`; folds into the
  pending Phase 4 live boot — reaching the `"Starting KGSM Bot service"` log proves the graph.
- **Follow-ups for the lib:** `git init` + first commit (it's currently just a dir with
  LICENSE/.gitignore); eventually publish to the github/nuget feed and drop the local source.

### Phase 5 — install / uninstall behind confirmation ✅ CODE-COMPLETE + unit-tested — ⚠️ NEVER BOOTED
The destructive, data-losing tier, gated behind a **model-independent Discord-button**
confirmation. **21 bot tests pass; builds clean; the library was NOT touched** (proof the
Phase-4→5 abstraction holds — `git status` in the lib repo is clean).
- **New destructive tier** in `LlmTools`: `install_server` (blueprint + optional instance
  name) and `uninstall_server` (instance). Offered only to authorized callers.
- **Resolve-before-confirm:** the model NEVER triggers a destructive op. `ToolDispatcher`
  resolves the target first (instance via the existing resolver; **blueprint** via a new
  `ResolveBlueprintAsync`), and ambiguous/unknown short-circuits to ask the user — *no*
  confirmation button is shown for an unresolved target. A resolved op is **staged**, not
  executed, and a staging message is returned to the model.
- **Staging mechanism (zero library change):** destructive tools pass the gate (uncounted
  against the 5-cap) so they reach the dispatcher, which records a `PendingConfirmation`
  into an **`IConfirmationContext`** — an `AsyncLocal`-backed per-turn sink. `ServerAssistant`
  opens the scope, runs the library loop, and **drains** the staged ops into an
  `AssistantResult { Text, Confirmations }`. The library's `ToolGate.Refuse` was enough; no
  "confirmation"/"defer" concept leaked into `TheKrystalShip.Llm`.
- **Confirmation UX:** `MessageHandler` posts a ⚠️ message with **Confirm (danger) / Cancel**
  buttons per staged op. `ConfirmationModule` (a `[ComponentInteraction]` handler) executes
  on click: it **re-authorizes the clicker** (must hold `ActionRoleId` — any member could
  click), **re-validates** the target against the live list, acks within Discord's ~3s window
  via `UpdateAsync` (clears buttons), does the kgsm work, then `ModifyOriginalResponseAsync`
  with the result. The customId encodes the resolved action (`kgsmcf~U~{inst}` /
  `kgsmcf~I~{bp}~{name}`); no server-side store.
- **Two blast-radius caps now exist (the user's "one prompt can't shuffle the library"):**
  mutating ops capped at **5/message** (counted, executed); destructive ops capped at
  **3 staged/message** (`MaxDestructiveStagedPerMessage`, proposals only). **OPEN DECISION:**
  N=3 is a placeholder — confirm the value you want (1/2/3).
- **System prompt** tells the model destructive ops are staged for human button-confirmation,
  so it proposes-and-stops and must NOT claim "done."

#### Phase 5 caveats / open items
- ⚠️ **NEVER BOOTED.** Phase 4 auth, runtime DI, AND the entire Phase 5 button/defer/
  click-reauth path have never run against live Discord/Ollama — unit tests structurally
  can't reach them. "21 tests + clean build" ≠ "working". **One live boot clears all three**
  (DI resolves at `host.Build()` → reach the `"Starting KGSM Bot service"` log; the Phase 4
  `canAct` log; a real uninstall click-through). This is THE next action.
- ⚠️ The **>3s install** case is where the `UpdateAsync`→work→`ModifyOriginalResponseAsync`
  deferral earns its keep — verify it edits the message after a long kgsm run rather than
  erroring on an expired token. Uninstall is fast and will look fine; install is the risk
  (and the kgsm 30s `ProcessRunner` timeout bites here too → a slow install can be killed
  mid-download). Don't trust install on a slow game until that timeout is raised in kgsm-lib.
- Minor: **Cancel is unauthenticated** (any member can dismiss a pending confirm — low-harm
  griefing; the action-holder can re-ask). The button customId splits on `~`, so a kgsm name
  containing `~` would mis-route — astronomically unlikely given filesystem naming.

### Key source files (post-extraction)
**Library — `/home/heisen/TheKrystalShip.Llm/TheKrystalShip.Llm/`:**
| Concern | Path |
|---|---|
| DTOs + Result | `Models/{LlmMessage,LlmResponse,LlmToolCall,LlmToolDefinition,Result}.cs` |
| Turn/policy | `Models/AgentTurn.cs` (`AgentTurn`, `ToolGate`) |
| Interfaces | `Interfaces/{ILlmClient,IConversationStore,IToolDispatcher,ILlmAgent}.cs` |
| Ollama client | `Ollama/{OllamaLlmClient,OllamaOptions}.cs` |
| Conversation memory | `Conversation/{InMemoryConversationStore,ConversationOptions}.cs` |
| Agent loop | `Agent/{LlmAgent,LlmAgentOptions}.cs` |
| DI | `Extensions/ServiceCollectionExtensions.cs` (`AddLocalLlm`) |
| Tests | `../TheKrystalShip.Llm.Tests/{LlmAgentTests,InMemoryConversationStoreTests}.cs` |

**Bot — `/home/heisen/kgsm-bot/src/`:**
| Concern | Path |
|---|---|
| kgsm policy + turn assembly | `KGSM.Bot.Discord/Llm/{IServerAssistant,ServerAssistant}.cs` (incl. mutating 5-cap + destructive 3-cap gate, `AssistantResult`) |
| Tool registry | `KGSM.Bot.Discord/Llm/LlmTools.cs` (ReadOnly/Mutating/Destructive/All, `IsMutating`/`IsDestructive`) |
| Dispatcher + resolvers | `KGSM.Bot.Discord/Llm/ToolDispatcher.cs` (impls lib `IToolDispatcher`; instance + blueprint resolvers; stages destructive ops) |
| Confirmation staging | `KGSM.Bot.Discord/Llm/Confirmations.cs` (`PendingConfirmation`, `IConfirmationContext`/`ConfirmationContext` AsyncLocal sink, `ConfirmationIds`) |
| Confirmation buttons (execute) | `KGSM.Bot.Discord/Commands/ConfirmationModule.cs` (`[ComponentInteraction]` click handler; re-auth + re-validate + execute) |
| System prompt | `KGSM.Bot.Discord/Llm/{ISystemPromptBuilder,SystemPromptBuilder}.cs` (destructive-staging guidance) |
| Discord I/O + auth | `KGSM.Bot.Discord/MessageHandler.cs` (posts confirmation buttons) |
| DI wiring | `KGSM.Bot.Infrastructure/DependencyInjection.cs` (`AddLocalLlm`), `KGSM.Bot.Discord/Program.cs` |
| Inventory cache | `KGSM.Bot.Infrastructure/KGSM/KgsmStateCache.cs` + invalidation in `KGSM.Bot.Application/Services/ServerEventCoordinatorService.cs` |
| kgsm impl | `KGSM.Bot.Infrastructure/KGSM/KgsmServerInstanceService.cs` |
| Bot tests | `tests/KGSM.Bot.Core.Tests/{Llm/ServerAssistantTests,Llm/LlmWiringTests,Llm/ToolDispatcherTests,Infrastructure/KgsmStateCacheTests}.cs` (21 tests incl. destructive caps + staging) |
| Feed config | `kgsm-bot/nuget.config` (local folder feed) |
