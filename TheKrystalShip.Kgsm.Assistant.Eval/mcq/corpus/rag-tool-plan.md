# RAG Tool — Design Plan

> **Status:** Design agreed, pre-implementation · **Created:** 2026-06-21
> **Companion docs:** [`assistant-toolbox-plan.md`](./assistant-toolbox-plan.md) (the toolbox this extends — §3.0/§3.2/§3.4/§3.6/§3.7 are load-bearing here), [`web-search-tool-plan.md`](./web-search-tool-plan.md) (the precedent: port + adapter + fail-closed gate)
> **Code:** `TheKrystalShip.Llm` (Ollama client + agent loop), `TheKrystalShip.Kgsm.Assistant` (tools/ports/policy), `*.Infrastructure` (adapters), `*.Cli` (indexing host), `*.Eval` (benchmark)

This file is the source of truth for **adding retrieval-augmented generation to the KGSM assistant**. Motivation: a benchmark (KB Evals, 20 Jun 2026) showed `gemma4:12b` jumping **77.8% → 95.9%** on technical-doc MCQs once the right passage is retrieved into context (oracle ceiling 99.3%) — and **12B + RAG beats 31B with no RAG (83.4%)**. Retrieval beats parameters, which is decisive on a VRAM-bound RTX 3060.

---

## 1 · What we're building

A single model-facing **`search`** tool — a §3.4 deterministic aggregator that queries a **local vector index first** and **falls back to Tavily web search** only when local similarity is poor. RAG is not a new subsystem: it is the `web_search` pattern (port → Infrastructure adapter → fail-closed default → config gate → ReadOnly tool) with the source swapped from "HTTP to Tavily" to "embed the query, search a local index."

**Why a tool, not always-on injection.** The assistant's traffic is mostly *operational* (`get_status`, `server_command`), not knowledge Q&A. The benchmark's always-retrieve model assumes every query is a doc question; ours aren't. Retrieval must be gated *per turn*, which on this architecture means a tool the model elects to call — exactly how it already elects `web_search` vs `get_status`. This honors §3.0 (model routes, backend retrieves).

**Self-contained in kgsm-llm.** No other repo changes. Embeddings are an Ollama concern; the vector store is a new Infrastructure component; the Discord/web surfaces inherit it for free (they render the assistant stream). Low blast radius — unlike the toolbox work's kgsm + kgsm-lib + bot lockstep.

---

## 2 · Locked decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | **One unified `search` tool**, local-first → web-fallback. `web_search` is **removed from the model-facing catalog**; `IWebSearch` (Tavily) demotes to an internal capability behind `search`. | §3.2 names overlapping tools as *the* small-model selection failure. Two "search-for-info" tools is that failure. One tool = one "look it up" decision; backend decides local-vs-web deterministically (§3.0/§3.4). Net model-facing count unchanged → no added selection tax. |
| **D2** | **Corpus is operator-configured**; default `Sources` = the kgsm ecosystem docs. Any dev points it at their own folders. | Matches the "gated by config" ethos; useful out of the box; small default corpus keeps the flat index (D4) valid. |
| **D3** | **RAG-as-tool**, not automatic pre-retrieval. | Mixed operational/knowledge workload; retrieval gated per turn (§3.0). |
| **D4** | **Managed flat cosine index** to start (in-memory `float[]`, brute-force top-k, persisted to a file). `sqlite-vec`/Qdrant are the scale-up path, not the start. | A few-thousand-chunk corpus is sub-ms and tens of MB. Zero native deps; no extra process competing for the CPU/RAM reserved for game servers. |
| **D5** | **`embeddinggemma`** default embedder (Google's Gemma-native on-device model; 768-dim, Matryoshka-truncatable) — owner pick 2026-06-21 to pair with gemma4. | Built for on-device RAG; coheres with the chat model. Needs its own task prompts (`title: none \| text:` docs, `task: search result \| query:` queries) — in `EmbeddingPrefixes`. `nomic-embed-text` / `bge-m3` are config alternatives. |
| **D6** | **The indexer is a standalone AOT daemon, decoupled from the assistant CLI** — its own binary over the shared core (§3.1), not a CLI mode. `--watch` (autonomous) + `--once` (manual). The Service only **loads** the index read-only (hot-reloads on swap); it never indexes. The CLI *hooks in* (one-shot via the core, or shell-out), it doesn't contain the engine. | Reactive + autonomous, true producer/consumer separation, all embedding work off the request path and off the game-server box's hot loop. |
| **D7** | **Fail-closed + omit-when-disabled.** `Rag:Enabled=false` default; when disabled the `search` tool is not even offered to the model (omit at `SelectTools`, like unauthorized command tools). | Don't burn the small model's selection budget on a dead tool. This is the "developers can disable it in config" requirement. |
| **D8** | **Incremental, content-hash re-index** (manifest diff → re-embed only changed/added/removed files) + **atomic, same-filesystem swap** (temp → `rename`); reader **loads-fully-then-closes** + hot-reloads. | A debounced daemon on a game-server box must not full-rebuild on every save — deltas only. Same-fs atomic swap + full-load means a separate reader process never sees a partial index and picks up updates live. |
| **D9** | **The decoupling contract is a versioned on-disk index** — header carries **format-version + embedding-model + dim + manifest**; the reader **rejects on any mismatch**. Shared `TheKrystalShip.Rag` core = convenience (stops reimplementation drift); the versioned format = the guarantee (stops version drift between independently-deployed binaries). | Two binaries built at different times must fail loud, not mis-read. |
| **D10** | **Embeddings live in the AOT-safe `TheKrystalShip.Rag` core, not on `ILlmClient`.** Two Ollama clients (chat = reflection/JIT in `TheKrystalShip.Llm`; embed = source-gen/AOT here) is **deliberate duplication**. Gate the AOT decision on **measured idle RSS + cold-start**, not the <10 MB binary target (aspirational; HttpClient+TLS dominate). | Chat doesn't need embeddings; the AOT daemon can't depend on the JIT Llm package. RSS is the real constraint for a long-running watcher. |

---

## 3 · Architecture

Producer/consumer split around one **AOT-safe shared core**, coupled only by a versioned on-disk index (D9):

```
  ┌─────────────── TheKrystalShip.Rag (AOT-safe core) ───────────────┐
  │  embed client (POST /api/embed, source-gen JSON)                  │
  │  structure-aware chunker · versioned index read/write · cosine    │
  └───────▲───────────────────────────────▲─────────────────────────-┘
          │ references                     │ references
  ┌───────┴────────┐                ┌──────┴─────────────────┐
  │ Indexer daemon │  writes index  │ Assistant IRetrieval   │
  │ (standalone    │ ─────────────► │ (JIT, Infrastructure)  │
  │  AOT binary)   │   (the file    │ query-embed + search   │
  └────────────────┘    = contract) └──────┬─────────────────┘
                                           │
                                  search(query)  ← §3.4 aggregator:
                                                   local-first, Tavily
                                                   (IWebSearch) fallback
```

1. **`TheKrystalShip.Rag` — the AOT-safe shared core.** Owns the embed client (`POST /api/embed` `{model,input}` → `{embedding:[…]}`, **source-generated JSON**, ~2 DTOs), the structure-aware chunker, the **versioned index read/write** (D9), and cosine search. AOT-clean (0 IL2026/IL3050), zero `kgsm-lib`/`TheKrystalShip.Llm` deps. **Embeddings deliberately do *not* go on `ILlmClient`/`OllamaLlmClient`:** chat doesn't need them, and the AOT daemon can't depend on the JIT Llm package. Two Ollama clients — chat (reflection/JIT, `TheKrystalShip.Llm`) and embed (source-gen/AOT, here) — is **intentional, justified duplication** (the same call you made hand-rolling the Ollama client over an SDK), not drift. A batch embed overload helps indexing throughput.

2. **Retrieval port** — `IRetrieval.RetrieveAsync(query, topK)` in the Assistant lib (sibling of `IWebSearch`), returning scored chunks. Fail-closed `DisabledRetrieval` default from `AddKgsmAssistant` (mirrors `DisabledWebSearch`); the real adapter (`VectorStoreRetrieval`, Infrastructure) — which **references the core** for query-embed + read + search and **rejects a format/model/dim mismatch** (D9) — is registered by `AddKgsmAdapters` only when `Rag:Enabled`. (Referencing an AOT-safe core from the JIT assistant is free — AOT-safe code runs under JIT.)

3. **Vector store + chunker — in the core (not Infrastructure).** Flat cosine index (D4) persisted to `IndexPath` via the core's versioned writer; the header stamps **format-version + embedding-model + dim + the incremental manifest** (D9, §6). **Structure-aware chunking** for markdown: don't split fenced code blocks; prepend header-breadcrumb context to each chunk; size/overlap configurable — the "fair bit of tweaking" the benchmark author flagged.

4. **`search` aggregator** — in the Assistant lib, a deterministic composer (§3.4): call `IRetrieval`; if top similarity ≥ `MinScore`, return local chunks; else (or as a top-up) fall back to `IWebSearch`. The `ToolDispatcher` `search` handler calls this; `web_search` is gone from the model's view. No nested model calls (§3.4).

5. **`RagOptions` gate** — in Infrastructure config alongside `WebSearchOptions` (see §4).

**Wiring touch-points** (from the code map):
- `LlmTools.cs` — remove `web_search` from `ReadOnly`, add `search`.
- `ToolDispatcher.cs` — replace the `web_search` case with a `search` case → the aggregator.
- `ServerAssistant.SelectTools` — omit `search` when `Rag:Enabled=false` **and** Tavily is also disabled (if Tavily is on but RAG off, `search` can still serve web-only — decide at build; simplest V1: `search` is offered iff *either* source is enabled).
- `AddKgsmAssistant` — register `DisabledRetrieval` + the `search` aggregator.
- `AddKgsmAdapters` — register `VectorStoreRetrieval` + bind `RagOptions` when enabled.

### 3.1 · The indexer — standalone AOT daemon, decoupled by the index file

The indexing engine is **its own artifact**, not a mode of the assistant CLI. A thin **AOT** host over the core:

- **Daemon (`--watch`)** — a **debounced** `FileSystemWatcher` over `Sources` → **incremental** re-index (D8: manifest diff by content hash, re-embed only changed/added/removed files) → **atomic swap**. Standalone binary, systemd-deployable, AOT for low idle RSS + no JIT warmup (the metric that matters on a box reserving RAM for game servers — *not* binary size; D10).
- **One-shot (`--once`)** — full (or delta) build, then exit. For the initial index or daemon-off.

**The decoupling contract is the index file, not a shared process.** Producer (daemon) and consumer (assistant retrieval) are two independently-deployable binaries that touch *only* the versioned index on disk (D9). The shared core stops *reimplementation* drift; the format-version header stops *version* drift between a daemon and assistant built at different times — the reader **rejects on mismatch**.

**Cross-process safety:** the temp file and the index must live on the **same filesystem** (POSIX `rename` is atomic only within one), and the reader **loads-fully-then-closes** so a swap mid-read is safe (the in-memory flat index already does this). Single-writer lock on `IndexPath` if a manual `--once` and the daemon could ever overlap.

**Hooking it to the kgsm-assistant CLI (V1, minimal):** the assistant retrieval already references the core, so a one-shot `index` verb on the CLI that calls the core is nearly free; the zero-shared-code alternative is the CLI shelling out to the daemon binary's `--once`. A unix-socket control plane (`reindex now` / `status`, mirroring the monitor's socket) is a real later nicety — that's "observe/poke the live daemon," not "hook it up" — **deferred**.

---

## 4 · Config (`RagOptions`)

```yaml
Rag:
  Enabled: false              # the gate (D7) — fail-closed
  EmbeddingModel: embeddinggemma
  IndexPath: ./rag-index.bin  # daemon temp + this file must share a filesystem (atomic rename, D8)
  Sources:                    # operator-configured (D2); default = kgsm docs
    - ./docs
  TopK: 5
  ChunkSize: 512
  ChunkOverlap: 64
  MinScore: 0.35              # below this, local is "no good hit" → web fallback / honest empty
  MaxContextChars: 6000       # cap injected grounding (protect the lean model's window)
  Watch:
    Enabled: false            # daemon mode (index --watch): re-index reactively on file changes
    DebounceMs: 2000          # coalesce bursts of edits before re-indexing
```

Layered the same way as the rest (embedded default → sidecar → `$KGSM_ASSISTANT_CONFIG` → `Section__Key` env → CLI flag). `EmbeddingModel` lives here (RAG owns it); `ILlmClient.EmbedAsync` takes the model name as a param so the client stays generic.

---

## 5 · Result shape & honesty

- **Fits the existing `ToolResult<K,D>` envelope (§5 of the toolbox plan).** `summary` = grounding text the model narrates over; `data` = chunks + source refs for the surface card; `links` = source doc refs; `confidence` reflects retrieval strength.
- **RAG inverts §3.6.** For status tools the model gets a lean summary only. For RAG the retrieved chunk *text* **is** the grounding — it must go into the model's context (capped by `MaxContextChars`). The structured chunk/source card still rides out-of-band to the surface per §5. V1 SSE `tool.result` carries `{tool, summary}` like everything else; the rich card defers until a surface consumes it.
- **Honesty (§3.7 principle).** If nothing clears `MinScore` and web fallback also returns nothing, `search` returns an explicit "no relevant sources found" — never a fabricated or low-relevance chunk dressed as an answer. The model says "I couldn't find that in the docs," it doesn't guess.
- **Provenance.** Carry source path + chunk location so the model can cite and the surface can link. Encourage the prompt to answer *from context and cite*.

---

## 6 · VRAM — the §3.2 re-decision (explicit, not an oversight)

§3.2 rejected "an embedding model" — but for *tool-relevance filtering*, a marginal gain not worth the VRAM. For RAG the embedder **is** the value (the +18 pt jump on 12B). The math fits: `gemma4:12b` ~6.3 GB + `embeddinggemma` ~0.6 GB inside 12 GB, with `OLLAMA_MAX_LOADED_MODELS=2` keeping both resident. Query-time cost is **one** tiny embed call; the heavy embedding pass is **offline indexing** (D6), run deliberately when convenient. The old lock does not bind here — recorded as a deliberate re-decision.

**Index invalidation guard (D9):** changing `EmbeddingModel` changes the vector space → the index is garbage against a new model. The versioned header stamps **format-version + embedding-model + dim**; the reader refuses to query (and prompts to re-index) on any mismatch — this also catches producer/consumer *version* drift between the independently-deployed daemon and assistant.

---

## 7 · Roadmap (dependency-ordered; each phase independently testable/shippable)

| Phase | Scope | Where | Proves |
|---|---|---|---|
| **1 — RAG core** | `TheKrystalShip.Rag` (AOT-safe): embed client (`/api/embed`, source-gen) + chunker + **versioned index read/write** + cosine search; tests + live embed smoke + **AOT publish (0 ILC warnings)** | new core lib | the foundation both the daemon and retrieval build on; embeddings live here, *not* on `ILlmClient` |
| **2 — Retrieval adapter** | `IRetrieval` + `DisabledRetrieval` over the core, `RagOptions`, DI gating, **mismatch-rejection on load** (D9) | Assistant + Infrastructure | retrieval works against a canned index — no model in the loop |
| **3a — Indexer (one-shot)** | standalone AOT host over the core: `--once` full/delta build; CLI hook (one-shot via core, or shell-out) | new AOT binary | turns docs into the index Phases 2/4 consume |
| **3b — Indexer daemon** | `--watch`: debounced `FileSystemWatcher` + incremental (manifest) re-index + atomic same-fs swap; Service hot-reloads; measure idle RSS/cold-start | AOT binary + Service | reactive, autonomous re-index — depends only on the core (P1), runs parallel to P4 |
| **4 — `search` aggregator** | `search` in `ReadOnly`, dispatcher handler, local-first/web-fallback compose, demote `web_search`, `SelectTools` gate | Assistant | end-to-end; live test on `gemma4:12b` like web_search |
| **5 — Ground-truth eval + tuning** | **new MCQ-accuracy mode** in the harness; tune chunk/TopK/MinScore | Eval | reproduces the no-RAG/with-RAG/oracle chart for *our* corpus |
| **6 — Quality levers** (later) | hybrid BM25+vector, re-ranker, file-watch / scheduled re-index | — | close the with-RAG→oracle gap |

> **SHIPPED 2026-06-21 (Phase 1 — `TheKrystalShip.Rag` core).** New AOT-safe lib in-repo (Option A), added to the slnx. Source-gen embed client (`OllamaEmbeddingClient` → `POST /api/embed`, `RagJsonContext`) with the **document/query asymmetry baked into the API** (`EmbedDocumentsAsync`/`EmbedQueryAsync` + `EmbeddingPrefixes`); structure-aware `MarkdownChunker` (headings→breadcrumb, code fences intact, size+overlap); versioned **binary** index (`RagIndexFile`: magic+format-version, atomic same-fs `WriteToFile`, `RagIndexFormatException`→rebuild); flat cosine `VectorSearch`. **22 tests green, 0 warnings** with `IsAotCompatible` analyzers on — incl. a `RagJsonContext` source-gen round-trip, mocked-HTTP embed tests, and a **live `embeddinggemma` smoke** (real Ollama 0.30.10, 768-dim, doc/query dims consistent). Default embedder is now **`embeddinggemma`** (Gemma-native, owner pick). Full solution builds (only pre-existing Llm/Assistant XML-doc warnings remain). **Committed on `main` (`dc8e6a3`).**

> **SHIPPED 2026-06-21 (Phase 2 — retrieval adapter).** `IRetrieval` + `RetrievedChunk` + fail-closed `DisabledRetrieval` in the Assistant core (`Ports/IRetrieval.cs`), TryAdd'd by `AddKgsmAssistant` — mirrors `IWebSearch` exactly. Infrastructure: `RagIndexProvider` (lazy-load + cache successful loads, re-attempt while the file is absent, **§D9 model-mismatch rejection**, and a never-throw catch ladder ending in a catch-all for valid-header/corrupt-body `FormatException`) and `RagRetrieval` (blank-query guard, the **`Rag.Models.Result`→`Llm.Models.Result` translation seam**, a **dimension guard** before `VectorSearch.TopK`, `MinScore` filter). `RagOptions` (`Enabled` default **false**, `IndexPath`/`TopK`/`MinScore`) binds the **same `"Rag"` section** as the core's `RagEmbeddingOptions`. **DI gating (§D7):** `AddKgsmAdapters` registers the concrete `IRetrieval` *only* when `Rag:Enabled=true` (last-wins over the default, given the `AddKgsmAssistant`→`AddKgsmAdapters` order); disabled → nothing registered, capability omitted. Disabled `Rag` block added to Service + CLI `appsettings.json`. **13 new tests** (canned-index integration, every fail-closed path, and a wiring-seam test for the silent D7 gate); **395 solution tests green, 0 warnings**. Two advisor passes hardened the never-throw contract and kept `MinScore` default 0 so the Phase 4 aggregator still sees the real top score; **hot-reload deferred to 3b** (drop-in spot marked in `RagIndexProvider.Get`). `IRetrieval` is registered but **not yet consumed** — wiring it to the model is Phase 4. **Committed on `main` (`580b62f`).**

> **SHIPPED 2026-06-21 (Phase 3a — indexer one-shot + CLI hook).** AOT-spiked first (advisor): a `PublishAot` exe over the core's source-gen embed client + `Microsoft.Extensions.Logging.Console` publishes **0 ILC warnings at ~6.7 MB** — validated before building the engine. **Core engine** (`TheKrystalShip.Rag/Indexing/`): `IndexBuilder.BuildAsync` (enumerate sources → per-file SHA-256 → **incremental** reuse of unchanged files' chunks+vectors from the previous index → chunk + batch-embed the rest → assemble → atomic `WriteToFile`), `IndexRunner` (shared host composition), and a never-throw `RagIndexFile.TryReadFromFile` for the previous-index load. Reuse is invalidated by a changed **model, dimension, or chunk-size/overlap** (a one-call dimension **probe** both fail-fasts on an unreachable embedder and rejects stale-dim reuse → clean full rebuild, never a mixed-dim write). **Standalone Native-AOT binary** `kgsm-rag-indexer` (`--once --source --index [--model --endpoint --pattern --chunk-size --chunk-overlap --full]`; `--watch` stubbed to a "Phase 3b" message). **CLI hook** `kgsm-assistant index [--full --source]` reads the `Rag` block and calls the same `IndexRunner`, branching *before* KGSM/backend wiring so a box with Ollama-but-no-kgsm can index. `RagOptions` gained `Sources`/`SourcePattern`/`ChunkSize`/`ChunkOverlap` (the unified §4 block); appsettings updated. Verified end-to-end against live Ollama (both hosts, incl. **cross-process incremental reuse**), plus a **cross-phase test** proving a builder-produced index is consumed by `RagIndexProvider` (§D9 check) + `RagRetrieval`. **417 solution tests green, AOT publish 0 warnings**, `bin/obj/publish` gitignored. **Committed on `main` (`5ea32ec`).**

> **SHIPPED 2026-06-21 (Phase 3b — indexer daemon + Service hot-reload).** Two advisor-reviewed halves. **(1) The `--watch` daemon.** `Indexing/CoalescingRebuildLoop` — a `Channel.CreateBounded<byte>(1, DropWrite)` used as a single coalescing dirty-bit; the loop is *wait-for-signal → settle (injected delay) → drain → rebuild*, so a burst of editor saves collapses to one rebuild while a change landing mid-rebuild still re-triggers (no lost update, at most one extra idempotent rebuild). A throwing rebuild is logged and the loop survives; cancel exits cleanly. The settle delegate is injected purely to make the loop **deterministically unit-testable without sleeps**. `Indexing/IndexWatcher` builds the embed client + `IndexBuilder` once, wires one `FileSystemWatcher` per source (a dir recursively, filtered to the pattern; a file via its parent + name filter), and points **every** event — including a buffer-overflow `Error` — at `loop.Signal()`. The watcher is deliberately **dumb**: it never tracks *which* file changed, because the rebuild re-enumerates and content-hashes all sources, so D8 incremental reuse decides what's actually re-embedded (verified live: add a file → "1 embedded, 1 **reused**"). **Host:** `IndexerArgs` gained `--watch` + `--debounce-ms` (default 750); `Program.cs` requires exactly one of `--once`/`--watch` (else rc=2), handles **SIGTERM** (`PosixSignalRegistration`, `using`-scoped) as well as SIGINT, exits **0** on a graceful stop (so systemd `Restart=on-failure` won't fire on an intended stop), and logs via **`AddSystemdConsole`** in daemon mode / `SimpleConsole`→stderr in one-shot (logging-convention conformance, advisor-caught — daemon output now carries `<N>` journald priorities). The CLI `index` verb stays **one-shot-only** (D6). **(2) Service hot-reload** in `RagIndexProvider.Get()`: a cheap stat (last-write + length) on every call detects the indexer's atomic swap and reloads. A failed reload (mid-swap, corrupt, or §D9 model-mismatched new build) **degrades to the last good index** rather than going dark — and the observed stamp advances on **every attempt, success or failure**, so a bad swap-in is read **once** (then fast-pathed) and a later good build still self-heals; `TryStamp` treats a missing file as "no version" (the deleted-file canary stays green). **Tests:** 4 deterministic `CoalescingRebuildLoop` (sleepless semaphore handshakes), 1 tolerant `FileSystemWatcher` smoke (fake embedder via an internal ctor seam, polls the on-disk index), 3 hot-reload (good swap / bad-swap-degrade-then-heal / delete), 2 args. **426 solution tests green, AOT publish 0 ILC warnings (~6.8 MB)**; live-verified end-to-end against real Ollama (initial build → add file → incremental re-index → SIGTERM exit 0). **Committed on `main` (`aa9ea91`).** Known startup gap: Ollama-down-at-boot means the initial build fails with no periodic retry, so the index stays stale until the next doc edit — the fix is operational (systemd `After=` ordering), deferred to the deploy/unit-file step. NEXT: **Phase 4** — the `search` aggregator (local-first → Tavily fallback; add `search` to the model-facing catalog, demote `web_search` to internal) — the step that turns all this plumbing into the 77.8→95.9% behavior.

> **SHIPPED 2026-06-21 (Phase 4 — the `search` aggregator).** The payoff: retrieval is now wired to
> the model. New in the Assistant lib: `Ports/ISearch.cs` + a public, deterministic `SearchAggregator`
> (§3.4, **no nested model calls**) — local retrieval first; a top hit at or above `SearchOptions.LocalMinScore`
> (0.35) answers from the docs **without a web call**; otherwise the web is tried; otherwise a weak local
> hit beats nothing (returned with a caveat); otherwise honestly empty — and a web **failure** is reported
> as "couldn't search", **never** as "nothing found" (the measured-or-unknown rule). `MaxContextChars`
> (6000) caps the local grounding and always keeps the strongest chunk. **Catalog/dispatch:** `LlmTools.Search`
> **replaces** `web_search` (removed from the model's view — `IWebSearch` is now an INTERNAL capability the
> aggregator composes); `ToolDispatcher` depends on `ISearch` and relays. **Gate/availability:** `SelectTools`
> omits `search` when neither a local index nor a web provider is configured (§D7 — and a client *requesting*
> it then gets the honest invalid-tool error); the per-message cap (`MaxSearchesPerMessage`=5) is now a
> loop-runaway guard (the per-day web spend ceiling stays host-side). `SearchOptions` binds the "Rag" section
> for thresholds; its `LocalEnabled`/`WebEnabled` are **computed** by `AddKgsmAdapters` (where both the RAG
> switch and the Tavily key are known) — the one place that decides whether `search` is offered. The
> Service `/tools` picker mirrors the omit. **Eval:** the harness force-enables availability so the E-group
> routing rubrics stay meaningful (retargeted `web_search`→`search`, corpus `v2`→`v3`), and a new
> `HarnessSearchAvailabilityTests` proves that link **by execution** (no model/kgsm). **436 solution tests
> green, 0 warnings**; a gated live test confirmed the real local-first path end-to-end (a clearly-relevant
> query scored **0.754** cosine, well above the 0.35 default). No sibling-repo breakage from removing the
> public `LlmTools.WebSearch`. **Committed on `main` (`fa29f3e`).** Not verified this session: the live **model**
> routing (does the model pick `search`? — the heavy eval run) and the gated `WebSearchLiveTests` (needs a
> real Tavily key). NEXT: **Phase 5** — the ground-truth MCQ-accuracy eval mode + threshold/chunking tuning.

**Phase 5 is a *new* mode, not a reuse.** `kgsm-assistant-eval` scores *routing* ("did it call the right tool?"), by design (it dodges the run-state P0). The lift chart is 100% **ground-truth answer correctness**: generate MCQs with a strong model, score correctness, add the oracle column (correct doc handed in directly). That's a new harness mode — don't let the roadmap imply the routing scorer gives the lift.

---

## 8 · Open / deferred

- **✓ Repo boundary — RESOLVED 2026-06-21 (A): new projects in the `kgsm-llm` repo** (`TheKrystalShip.Rag` core + the indexer host). Lighter setup; the AOT/ILC discipline applies to the **new projects only** (the rest of `kgsm-llm` stays JIT). The core is a library, so a later spin-out to a leaf repo (`kgsm-rag`, the rejected option B) stays cheap if it earns independence.
- **`search` offering when only one source is enabled** — V1: offer `search` iff RAG *or* Tavily is enabled; if only Tavily, it's web-only behind the same verb. Confirm at build.
- **Indexer write coordination** — daemon owns writes in steady state; the manual one-shot is for daemon-off. If both coexist, a single-writer lock on `IndexPath`; readers stay safe via the atomic swap (D8). Scheduled/periodic refresh (beyond file-watch) stays a later option.
- **Hybrid + re-ranker** — Phase 6, driven by the measured with-RAG→oracle gap, not speculatively.
- **Secret redaction in retrieved chunks** — low risk for kgsm docs; revisit if game-config or private corpora get indexed.
