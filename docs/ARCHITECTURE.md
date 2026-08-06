# Architecture — kgsm-llm

The mental model for understanding this repo cold. For *running* it see
[`DEPLOYMENT.md`](./DEPLOYMENT.md); for *config keys* see [`CONFIGURATION.md`](./CONFIGURATION.md).

## The one-paragraph version

A local LLM (Ollama) runs a **tool-calling agent loop**. The loop itself is generic and lives in
`TheKrystalShip.Llm` — it knows how to talk to the model and round-trip tool calls, but nothing
about KGSM. `TheKrystalShip.Kgsm.Assistant` is the **brain**: it defines the tool catalog, the
system prompt, the action policy, and a set of **ports** (interfaces) for the things tools need.
`*.Infrastructure` supplies the **adapters** that bind those ports to reality — the kgsm engine
(via kgsm-lib), Tavily web search, a direct URL fetch, and a local RAG index. Two **surfaces** drive the same brain:
an HTTP/SSE **Service** (for the web SPA) and a terminal **CLI**. A separate, self-contained
**RAG** subsystem (`TheKrystalShip.Rag` + the indexer) produces a doc index the brain can search.

## Layer cake

```
            ┌─────────────────────────────┐   ┌─────────────────────────────┐
 surfaces   │  Service (HTTP/SSE + auth)   │   │  CLI (one-shot / pipe / REPL)│
            └──────────────┬──────────────┘   └──────────────┬──────────────┘
                           └───────────────┬─────────────────┘
                                           ▼
            ┌──────────────────── TheKrystalShip.Kgsm.Assistant ───────────────────┐
 brain      │  tool catalog · system prompt · action policy (propose/confirm)       │
            │  the `search` aggregator · fetch_url · PORTS: IRetrieval, IWebSearch, │
            │  IWebFetch, kgsm ops                                                  │
            └──────────────┬───────────────────────────────────────┬───────────────┘
                           ▼ (generic loop)                         ▼ (adapters)
            ┌──────────────────────────┐      ┌──────────────────────────────────────┐
 loop/core  │   TheKrystalShip.Llm      │      │  *.Infrastructure                     │
            │   Ollama client · agent   │      │  kgsm-lib graph · Tavily · fetch ·    │
            │   loop · conversation mem │      │  RAG read                             │
            └──────────────────────────┘      └───────────────┬──────────────────────┘
                                                               │
                                                   ┌───────────┴───────────┐
                                                   ▼                       ▼
                                          kgsm-lib (engine)        TheKrystalShip.Rag
                                          (the chokepoint)         (read the .krag index)
                                                                          ▲
                                                          writes │ versioned .krag file
                                                          ┌──────┴───────────────────┐
                                                          │ TheKrystalShip.Rag.Indexer│
                                                          │ (standalone AOT daemon)   │
                                                          └───────────────────────────┘
```

## Why it's split this way

- **The loop is application-agnostic.** `TheKrystalShip.Llm` is publishable on its own (a Discord
  bot in a sibling repo consumes it as a package). It owns the model round-trip, the iteration
  cap, tool-output truncation, and conversation memory — and is handed the tools, prompt, and
  per-call authorization *by the host, every turn*. It never learns what a tool means.
- **Ports/adapters keep the brain testable and the engine swappable.** The brain depends on
  interfaces (`IRetrieval`, `IWebSearch`, `IWebFetch`, the kgsm command/query ports); Infrastructure
  provides concrete adapters. Disabled capabilities get **fail-closed null adapters**
  (`DisabledRetrieval`, `DisabledWebSearch`, `DisabledWebFetch`) registered by default, so the graph
  composes even when RAG/Tavily/fetch are off — the real adapter is registered *after* and wins only
  when configured. This is why the Service boots fine with nothing but Ollama + kgsm configured.
- **One brain, many surfaces.** The Service and CLI both compose the same three DI calls —
  `AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` — and differ only in how they get the
  prompt in and the answer out, and in *who the user is* (see [auth](#authentication--authority)).

## The agent turn

Every turn, the **host** (Service or CLI) decides policy and hands it to the loop:

1. **Build the system prompt fresh** — persona + the live instance/blueprint list injected, so the
   model reasons over *this host's* servers (a load-bearing trick: the model picks the right server
   from context instead of guessing).
2. **Choose the tool whitelist** — read-only vs full, based on the user's authority. The offered
   set *is* the whitelist.
3. **Provide a per-call gate** — a closure that authorizes each tool call (and can hold state, e.g.
   an actions-per-message cap).
4. The loop calls Ollama, dispatches any tool calls through the `IToolDispatcher` (which refuses
   unknown tools and never throws — failures come back as strings the model can recover from),
   feeds results back, and iterates up to `MaxIterations`.

### Action safety: propose, then confirm

Mutating operations (start/stop/install/config edits) are **never executed inside a turn**. A tool
call *stages* the action and returns a confirmation token; the user must explicitly confirm
(`/confirm` on the Service, an interactive y/N in the CLI). Tokens are **stateless HMACs** — they
survive a Service restart iff the signing key (`Assistant:Confirmation:Key`) is stable. A run with
no confirmation never touches a server.

### The `search` tool (RAG + web)

There is a single model-facing `search` tool backed by a deterministic **aggregator** (no nested
model calls): it queries the local RAG index first; a hit at/above `LocalMinScore` answers from the
docs; otherwise it falls back to Tavily; otherwise it returns an honest "nothing found" (and a web
*failure* is reported as "couldn't search," never as "nothing exists" — the ecosystem's
measured-or-unknown rule). `web_search` is internal; the model only ever sees `search`.

### The `fetch_url` tool (direct page read)

`search` finds pages via a provider's summarized hits; `fetch_url` reads the full text of ONE page
the model already has (or just found) the URL for — an official docs page, a Steam store page, a raw
Dockerfile. Backed by `IWebFetch`/`HttpWebFetch`, gated by its own `WebFetch:Enabled` flag (no API key
needed) and offered only when enabled. Because the fetched URL is model/user-influenced and this host
is internet-exposed, the adapter enforces a scheme allowlist (http/https only) and an SSRF guard that
rejects loopback/private/link-local/multicast/reserved addresses (including the `169.254.169.254`
cloud-metadata address), re-validated on EVERY redirect hop — auto-redirect is disabled and the
adapter follows redirects manually so each hop re-enters the guard. A size cap, a timeout, and
content-type filtering (HTML → extracted text; `text/plain` and similar pass through; binary types are
refused) round it out. See `docs/CONFIGURATION.md`'s `WebFetch` section for every knob.

## The RAG subsystem (producer/consumer)

RAG is deliberately decoupled into two independently deployable halves coupled by **one on-disk
file**:

- **Producer — `TheKrystalShip.Rag.Indexer`** (standalone Native-AOT binary): walks a docs corpus,
  chunks structure-aware (markdown headings → breadcrumbs, code fences kept intact), embeds via
  Ollama, and writes a **versioned `.krag`** index. Incremental by content hash; `--watch` rebuilds
  on change and **atomically swaps** the file.
- **Consumer — `TheKrystalShip.Rag` (read path)**, used by the Service/CLI via the `IRetrieval`
  adapter: loads the index, **hot-reloads** on swap, and degrades to the last-good index on a
  bad/mid-swap read rather than going dark.
- **The contract is the file's versioned header** (format version + embedding model + dimension +
  chunk params). A mismatch (e.g. a different embedder) is **rejected on load** — a different model
  is a different vector space, so stale vectors are never silently mis-read. This is what lets the
  two binaries be built/deployed independently.

**Why the core is AOT.** The indexer is a long-running daemon on a box that reserves RAM for game
servers, so it's Native-AOT (low idle RSS, no JIT warmup) — which forces `TheKrystalShip.Rag` to be
AOT-clean (source-generated JSON, zero reflection). The JIT assistant references the same core for
the read path (AOT-safe code runs fine under JIT). Embeddings deliberately live here, **not** on
`ILlmClient`: chat doesn't need them, and the AOT daemon can't depend on the JIT Llm package — two
Ollama clients (chat = JIT, embed = AOT) is intentional, justified duplication.

## Authentication & authority

- **CLI:** the shell user is the trust boundary. Read-only by default; `--read-only` opts down.
  No accounts.
- **Service:** Discord **OAuth**, run end to end by this service, → session JWTs it mints and can
  revoke. Authority is the ecosystem's ordered tier (`admin ⊇ operator ⊇ viewer`) resolved from the
  shared role map via the bot token and cached briefly, so the same person holds the same authority
  here, in the Control Panel and in the Discord bot. Acting needs `operator`; reviewing someone
  else's conversations needs `admin`; every guild member can read. A trusted-relay header path
  exists for a co-located aggregator (e.g. kgsm-api) but cannot escalate authority. Sessions are
  rows in SQLite, so they survive a restart and can be killed before their tokens expire.

## Deployment shapes (and why they differ)

| Artifact | Shape | Why |
|----------|-------|-----|
| Service | **framework-dependent** .NET 10 | Long-lived server on a host that already has the SDK/runtime; tiny artifact, fast patching |
| CLI | **framework-dependent** .NET 10 | Same runtime is present; small download |
| Indexer | **Native-AOT** standalone ELF | Resident daemon; low idle RSS + no JIT warmup matter more than anything, and it has no runtime dependency to assume |

See [`DEPLOYMENT.md §4`](./DEPLOYMENT.md#4--publish-the-artifacts) for the exact publish commands
and observed sizes.

## Ecosystem boundary

This repo is a **leaf** in the KGSM ecosystem. It reaches the engine **only** through **kgsm-lib**
(the single C#↔engine chokepoint) — it never shells out to `kgsm.sh` itself, and never opens the
watchdog socket directly. It depends on nothing but kgsm-lib + a local Ollama, and runs fully
standalone (no other ecosystem service required). It also honors the ecosystem's **measured-or-
unknown** rule: the assistant never fabricates a status or metric — if it can't determine
something, it says so. The workspace-level `system-architecture.md` is the keystone map for how all
the `kgsm-*` repos wire together.
