# kgsm-llm

The **LLM / assistant** layer of the [KGSM](https://github.com/TheKrystalShip) game-server
ecosystem: a local, tool-calling AI assistant that answers questions about — and (with
authorization) acts on — the game servers a `kgsm` engine manages. It runs entirely on a local
**Ollama** model (no cloud LLM), so it can live on the same VRAM-budgeted box as the servers.

> **New here? Start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for the mental model,
> then [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) to stand it up.

## What's in this repo

One shared backend (Ollama agent loop → KGSM tools → kgsm-lib) exposed through several surfaces,
plus a self-contained RAG subsystem:

| Project | Role |
|---------|------|
| **`TheKrystalShip.Llm`** | Generic Ollama tool-calling **agent loop** library (transport, memory, loop). Knows nothing about KGSM. → [README](TheKrystalShip.Llm/README.md) |
| **`TheKrystalShip.Kgsm.Assistant`** | The KGSM **brain**: tool catalog, ports, system prompt, action policy, the `search` aggregator |
| **`TheKrystalShip.Kgsm.Assistant.Infrastructure`** | **Adapters** that bind the ports to reality — kgsm-lib, Tavily web search, the RAG index |
| **`TheKrystalShip.Kgsm.Assistant.Service`** | 🚀 **HTTP/SSE turn API** (for the web SPA), Discord-OAuth auth → [README](TheKrystalShip.Kgsm.Assistant.Service/README.md) |
| **`TheKrystalShip.Kgsm.Assistant.Cli`** | 🚀 **`kgsm-assistant-cli`** terminal app (one-shot / pipe / REPL) → [README](TheKrystalShip.Kgsm.Assistant.Cli/README.md) |
| **`TheKrystalShip.Rag`** | AOT-safe **RAG core**: embed client, chunker, versioned index, cosine search |
| **`TheKrystalShip.Rag.Indexer`** | 🚀 **`kgsm-rag-indexer`** — standalone Native-AOT indexer daemon → [README](TheKrystalShip.Rag.Indexer/README.md) |
| **`TheKrystalShip.Kgsm.Assistant.Eval`** | Reproducible **benchmark** (routing + ground-truth RAG accuracy) → [README](TheKrystalShip.Kgsm.Assistant.Eval/README.md) |

🚀 = a **deployable** (the rest are libraries). Each `*.Tests` project is its xUnit suite.

## Quick start (CLI, ~5 minutes)

The fastest end-to-end check. Assumes the `kgsm-lib` sibling repo, the .NET 10 SDK, Ollama with
`gemma4:12b`, and a local `kgsm.sh` — the [deployment runbook](docs/DEPLOYMENT.md) covers each.

```bash
# In the tks workspace, with kgsm-lib checked out alongside this repo:
cd ~/tks/kgsm-llm
dotnet build TheKrystalShip.Llm.slnx -c Release
# Note: -c Release on the run reuses the build above, so stdout is just the answer
# (a bare `dotnet run` rebuilds in Debug and prints build output first).
KGSM__Path=/usr/local/bin/kgsm \
  dotnet run -c Release --project TheKrystalShip.Kgsm.Assistant.Cli -- "How many servers do I have, and what are they?"
# → a plain-English answer derived from a real kgsm tool call, e.g.:
#   "You have 2 servers currently installed: factorio-test and terraria-hardmode."
```

For the HTTP service, the RAG indexer, systemd, secrets, and reverse-proxy/TLS, follow
**[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** start to finish.

## Documentation

| Doc | What it's for |
|-----|---------------|
| **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** | How the layers fit; the agent loop, ports/adapters, RAG producer/consumer split; ecosystem context |
| **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** | Cold-start runbook: prerequisites → build → publish → run (CLI, Service, indexer) → verify |
| **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)** | Every config section/key/default, the env-var form, the secrets list |
| **[deploy/](deploy/)** | Ready-to-copy systemd units + an env-file template |
| Per-project `README.md` / `CLAUDE.md` | Surface-specific usage and design notes |
| [docs/m7-sse-5a-spec.md](docs/m7-sse-5a-spec.md) | The Service `/turn` SSE event contract (for SPA integration) |

## Where this sits in the ecosystem

```
kgsm (bash engine) ──┐
kgsm-watchdog ───────┤── kgsm-lib (the only C#↔engine chokepoint) ──┐
                     ┘                                              │
                          ┌───────────────────────────────────────┘
                          ▼
   ┌──────────────── kgsm-llm (this repo) ───────────────┐
   │  Llm loop → Assistant brain → Infrastructure adapters│
   │      ├── Service (HTTP/SSE) ──► web SPA (kgsm-web/api)│
   │      └── CLI (terminal)                               │
   │  Rag core ◄── Indexer daemon (writes the index file) │
   └──────────────────────────────────────────────────────┘
```

This repo **never** shells out to `kgsm.sh` directly — all engine access goes through
**kgsm-lib** (a project reference to a sibling checkout; see [deployment §1.1](docs/DEPLOYMENT.md#11-the-kgsm-lib-sibling-repo-build-time--do-this-first)).
It depends only on kgsm-lib + a local Ollama, and runs fully standalone (no other ecosystem
service required). The broader map lives in the workspace's `system-architecture.md`.

## Build & test

```bash
dotnet build TheKrystalShip.Llm.slnx -c Release
dotnet test  TheKrystalShip.Llm.slnx                 # ~500 tests, hermetic; live-Ollama smokes are inert without KGSM_LIVE_OLLAMA=1
```

The `TheKrystalShip.Rag*` projects are Native-AOT-clean — publishing the indexer must emit
**0 ILC warnings** (`dotnet publish TheKrystalShip.Rag.Indexer -c Release -r linux-x64`).

## License

GPL-3.0-only. See [LICENSE](LICENSE).
