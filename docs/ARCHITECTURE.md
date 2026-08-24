# Architecture — kgsm-llm

The mental model for understanding this repo cold. For *running* it see
[`DEPLOYMENT.md`](./DEPLOYMENT.md); for *config keys* see [`CONFIGURATION.md`](./CONFIGURATION.md).

## The one-paragraph version

A local LLM runs a **tool-calling agent loop**. The loop itself is generic and lives in
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
            │   LLM client · agent      │      │  kgsm-lib graph · Tavily · fetch ·    │
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
  when configured. This is why the Service boots fine with nothing but a model server + kgsm configured.
- **One brain, many surfaces.** The Service and CLI both compose the same three DI calls —
  `AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` — and differ only in how they get the
  prompt in and the answer out, and in *who the user is* (see [auth](#authentication--authority)).

## The agent turn

Every turn, the **host** (Service or CLI) decides policy and hands it to the loop:

1. **Build the system prompt fresh** — persona + the live instance/blueprint list injected, so the
   model reasons over *this host's* servers. This is load-bearing, and measured: without the lists
   in the prompt the model spends its first round-trip calling `list_instances` to ground itself
   before acting on a name it cannot verify — disabling thinking does not change the behaviour, and
   reordering the tools rules out positional bias — while with them injected it routes directly to
   the right tool, saving a full model pass on a single-GPU, latency-sensitive host.
2. **Choose the tool whitelist** — read-only vs full, based on the user's authority. The offered
   set *is* the whitelist.
3. **Provide a per-call gate** — a closure that authorizes each tool call (and can hold state, e.g.
   an actions-per-message cap).
4. The loop calls the model, dispatches any tool calls through the `IToolDispatcher` (which refuses
   unknown tools and never throws — failures come back as strings the model can recover from),
   feeds results back, and iterates up to `MaxIterations`.

### Action safety: propose, then confirm

Mutating operations (start/stop/install/config edits) are **never executed inside a turn**. A tool
call *stages* the action and returns a confirmation token; the user must explicitly confirm
(`/confirm` on the Service, an interactive y/N in the CLI). A run with no confirmation never touches
a server.

The Service holds the staged operation itself, in `pending_confirmations` alongside the conversation
history, and the token a client receives is an opaque 32-character handle onto it — single-use,
bounded by `Assistant:Confirmation:TtlSeconds`, and redeemable only by the user it was staged for.
Being durable, a staged action survives a Service restart. The CLI needs none of this: it holds the
`PendingConfirmation` in memory for the length of one prompt.

**The reply is held against the turn.** Staging is what makes an action real, and the model's account
of its own turn is not always right — it sometimes answers a mutating request conversationally and
reports the action as staged anyway. Such a claim can move nothing, but it misinforms: the user waits
on a confirmation prompt that was never posted. So on a turn that staged nothing and ran nothing, a
first-person claim of a staged or completed action is false by construction (`UnbackedActionClaim`).
The check runs only on that turn shape, so it can never contradict a real action; the auto-accept
path, which runs a command without staging one, records that it acted. Offers and reports of world
state are honest and pass through untouched.

The claim is caught where the turn can still answer it. `AgentTurn.ReviewReply` — the outbound
counterpart of the per-call `ToolGate` — puts each candidate reply to the host before the turn is
recorded, and `ServerAssistant` answers accept, amend, or re-prompt once. The first unbacked claim
re-prompts: the model is told, mid-turn, that it called no tool and that nothing is staged, with the
request restated. Only a second one is corrected and left standing. The correction is part of the
recorded reply, so a surface reading the turn back describes it exactly as the person who watched it
saw it — and on the streaming path both the notice and the correction are emitted as tokens, since
the claim has already reached the screen.

**A replayed turn keeps its shape.** The model's context is a projection of the history
(`ModelContextProjection`): the prompt, the tool calls the turn made, and the reply. The transcript
is also the set of examples the model imitates, so a turn that answered "start the server" by calling
a tool has to replay as a tool call — replayed as prose alone it teaches that the same request is
answered by describing the action instead of taking it, and the next reply narrates a staging that
never happened. A past call's OUTPUT is not replayed: each is a reading of a world that has moved on,
and a stale reading offered as current is a fabricated status, so every replayed call stands against
a placeholder that says so and asks for a fresh call.

### The `write_file` tool (an edit, not a file)

Two tools change a server's settings and they are not interchangeable: `set_config_value` writes one
key in KGSM's own `.config.ini`, and `write_file` changes the **game's** own config — Palworld's
`PalWorldSettings.ini`, `server.properties`, a mod's settings file.

`write_file` carries an **edit**. The model sends `old_string` (the exact text to replace, as
`read_file` returned it) and `new_string`; `PrepareInstanceFileEditAsync` reads the file through the
jail and applies that one replacement, and the resolved content is what gets staged. The file's other
bytes never enter the model's context in either direction, which is the property the tool exists for:
a model asked to reproduce a 3.5 KB config will drop settings, truncate keys and flip values, and a
confirmation is only as safe as the payload behind it.

The anchor must match **exactly once**. No match, several matches, an empty anchor, or a replacement
identical to what it replaces stages nothing and returns the reason, so an approximate edit never
becomes a proposal. `copy_from` covers the empty-or-absent config whose defaults live in a reference
file beside it: the reference is copied server-side and the replacement applied to the copy.

Reading a file to resolve an edit is capped at 1 MB rather than the 64 KB model-facing read cap —
those bytes go to the confirmation, not into a prompt — so a file too large to read in full can still
have one setting changed. Downstream nothing is special: the staged confirmation holds the complete
new content, the `command.proposed` frame carries it for a diff, and only a confirmation writes it.

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
  the configured embedding server, and writes a **versioned `.krag`** index. Incremental by content hash; `--watch` rebuilds
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
client stacks (chat = JIT, embed = AOT) is intentional, justified duplication. Each stack carries
its own provider switch, so the chat model and the embedder are pointed at their servers separately.

## The inference backend is one registration

`ILlmClient` (chat) and `IEmbeddingClient` (embeddings) are the only things the rest of the code
sees. Two implementations stand behind each — Ollama's native API and llama.cpp's
OpenAI-compatible one — and `Llm:Provider` / `Rag:Provider` decide which is registered, once, at
startup. The agent loop, the compactor, the tool catalog and the eval harness are identical either
way.

The wire formats are not equivalent, and the differences live entirely in the two clients:

- **Tool calls.** Ollama returns them complete in one frame. The OpenAI format streams a call's
  arguments as string fragments keyed by index, so `LlamaCppStreamParser` accumulates them and
  emits the assembled set in one frame, matching Ollama's shape.
- **Tool results.** Ollama addresses one by tool name; OpenAI addresses it by the id of the call it
  answers. `LlmMessage` carries no id, so `LlamaCppRequestBuilder` assigns them per request by
  walking the history — the same history always yields the same ids, so nothing is persisted.
- **The context window.** Ollama takes it per request. llama-server fixes it at launch and ignores
  a per-request value, so `Llm:ContextWindow` is not sent there; it is read to stamp token
  accounting, and must match the server's `-c`.
- **Tool calling has to be switched on.** llama-server needs `--jinja` and a tools-capable chat
  template. Without it the `tools` array is accepted and no tool call is ever emitted, which reads
  as an unhelpful model rather than a broken configuration.

## The default chat model: `gemma4:12b`

`Ollama:Model` defaults to `gemma4:12b`, chosen on a measured bake-off against the other
tool-calling candidate that fits the reference 12 GB card, `qwen3.5:9b` (the next Qwen
generation's smallest variant is 17 GB and does not fit). Over an 18-prompt tool-routing harness
mirroring the assistant's ops — clean commands, slang, typos, query-vs-command, chitchat that must
call nothing, multi-intent, prompt injection — gemma picks the correct tool with correct arguments
on 15 of 16 tool-expecting prompts (qwen: 14), and it refuses an "uninstall everything" injection
prompt that qwen answered with seven uninstall calls. That refusal is a bonus layer, never the
guarantee: action safety lives in the dispatcher's whitelist, the authority tiers and the
propose-then-confirm flow, which hold regardless of what the model emits.

## Authentication & authority

- **CLI:** the shell user is the trust boundary. Read-only by default; `--read-only` opts down.
  No accounts.
- **Service:** a KGSM password or a connected Discord account, run end to end by this service, →
  session JWTs it mints and can revoke. Authority is the ecosystem's ordered tier
  (`admin ⊇ operator ⊇ viewer`) held by the caller's KGSM account and re-read per request, so the same
  person holds the same authority here, in the Control Panel and in the Discord bot — all three read
  one record. Acting needs `operator`; reviewing someone else's conversations needs `admin`. A
  trusted-relay header path exists for a co-located aggregator (e.g. kgsm-api) but cannot escalate
  authority. Sessions are rows in SQLite, so they survive a restart and can be killed before their
  tokens expire.

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
watchdog socket directly. It depends on nothing but kgsm-lib + a local model server, and runs fully
standalone (no other ecosystem service required). It also honors the ecosystem's **measured-or-
unknown** rule: the assistant never fabricates a status or metric — if it can't determine
something, it says so. The workspace-level `system-architecture.md` is the keystone map for how all
the `kgsm-*` repos wire together.
