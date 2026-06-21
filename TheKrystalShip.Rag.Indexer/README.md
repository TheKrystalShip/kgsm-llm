# TheKrystalShip.Rag.Indexer (`kgsm-rag-indexer`)

The **RAG indexer** — a standalone Native-AOT daemon that turns a docs corpus into the versioned
vector index the assistant searches. It is the *producer* half of the RAG subsystem; the Service
and CLI are the *consumers*. The two are coupled by exactly one thing: the on-disk `.krag` file.

- How RAG fits together: [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md#the-rag-subsystem-producerconsumer)
- Deploy it (`--watch` under systemd): [`../docs/DEPLOYMENT.md`](../docs/DEPLOYMENT.md#8--rag-optional) + [`../deploy/`](../deploy/)

## Why it's a separate AOT binary

It's a long-running daemon on a box that reserves RAM for game servers, so it's **Native-AOT**
(low idle RSS, no JIT warmup) and has **no .NET runtime dependency** — a standalone ELF you can
drop next to anything. It shares the AOT-clean `TheKrystalShip.Rag` core with the assistant's read
path, but runs as its own process so embedding work stays off the request path. (The CLI also
exposes the same engine as `kgsm-assistant index` for a quick one-shot; the daemon is the
production path.)

## Build

```bash
dotnet publish TheKrystalShip.Rag.Indexer/TheKrystalShip.Rag.Indexer.csproj \
  -c Release -r linux-x64 -o out/indexer
# → out/indexer/kgsm-rag-indexer  (~7 MB ELF, expect 0 ILC warnings)
```

A nonzero ILC-warning count means an AOT-incompatibility slipped into the RAG core — fix it before
shipping; AOT-cleanliness is the whole reason the core is structured the way it is.

## Usage

Pick exactly one mode (`--once` or `--watch`), at least one `--source`, and an `--index`:

```bash
# One-shot build (initial index, or daemon-off hosts):
kgsm-rag-indexer --once  --source /opt/kgsm-assistant/docs --index /var/lib/kgsm-assistant/rag-index.krag

# Daemon: build once, then re-index on change until SIGINT/SIGTERM (run under systemd):
kgsm-rag-indexer --watch --source /opt/kgsm-assistant/docs --index /var/lib/kgsm-assistant/rag-index.krag
```

| Flag | Default | Notes |
|------|---------|-------|
| `--once` / `--watch` | — | **Required**, exactly one. `--watch` is the systemd daemon. |
| `--source <path>` | — | **Required**, repeatable. File or dir (dirs walked recursively). |
| `--index <file>` | — | **Required**. Output path; its **directory must share a filesystem** with the index (atomic temp→rename swap). |
| `--model <tag>` | `embeddinggemma` | Embedding model. **Must match** the consumer's `Rag:EmbeddingModel`. |
| `--endpoint <url>` | `http://localhost:11434` | Ollama base URL for embeds. |
| `--pattern <glob>` | `*.md` | File glob when walking directories. |
| `--chunk-size <n>` | `2000` | Chunk target (chars). Changing it forces a full rebuild. |
| `--chunk-overlap <n>` | `200` | Chunk overlap (chars). |
| `--timeout <s>` | `120` | Embed request timeout. |
| `--debounce-ms <n>` | `750` | (`--watch`) coalesce a burst of edits before rebuilding. |
| `--full` | — | (`--once`) ignore the existing index and rebuild from scratch. |
| `--verbose` | — | Debug logs on stderr. |

**Exit codes:** `0` ok (or graceful daemon stop) · `1` runtime failure · `2` usage error ·
`130` cancelled (one-shot Ctrl-C). **Logging:** `--watch` uses journald (`<N>` prefixes); `--once`
uses a stderr console.

## How rebuilds work

- **Incremental by default:** an existing `--index` is reused for files whose content hash is
  unchanged; only new/changed/removed files are re-embedded. (`--watch` re-enumerates and
  content-hashes every pass — it doesn't track *which* file changed, so a burst collapses to one
  rebuild and a change landing mid-rebuild still re-triggers.)
- **Automatic full rebuild** when the embedding model, vector dimension, or chunk size/overlap
  differs from the existing index (the versioned header catches it) — never a mixed-dimension write.
- **Atomic swap:** the new index is written to a temp file in the same directory and `rename`d into
  place, so a consumer reading concurrently never sees a partial file.

## The index contract (`.krag`)

A versioned binary: magic + format version + embedding model + dimension + chunk params + the
embedded chunks. The consumer (`TheKrystalShip.Rag` read path, used by the Service/CLI) loads it,
**hot-reloads** on swap, and **rejects on a model/dimension/format mismatch** — a different
embedder is a different vector space, so stale vectors are refused rather than silently mis-read.
Point the consumer's `Rag:IndexPath` at the same file you pass to `--index`.

## Known gap: Ollama-down-at-boot

The daemon embeds via Ollama at startup; if Ollama is unreachable then, the **initial build fails
with no periodic retry** — the index stays stale until the next doc edit (which re-triggers a
rebuild). The fix is operational: order the unit `After=ollama.service` (already set in
[`../deploy/kgsm-rag-indexer.service`](../deploy/kgsm-rag-indexer.service)). If Ollama isn't a
systemd unit on your host, ensure it's up before the indexer, or run a manual `--once` afterward.
