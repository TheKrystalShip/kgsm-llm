# Deployment & Operations — kgsm-llm

The cold-start runbook: from a fresh clone to a running assistant, with a copy-paste
acceptance check after every step. If a command here doesn't behave as shown, stop and fix
it before moving on — each step assumes the previous one passed.

- **What you're deploying** and how the pieces fit: [`ARCHITECTURE.md`](./ARCHITECTURE.md)
- **Every config key**, its default, and its env-var form: [`CONFIGURATION.md`](./CONFIGURATION.md)
- **systemd units + an env template** ready to copy: [`../deploy/`](../deploy/)

This repo ships **three deployables** over one shared backend:

| Artifact | Binary | What it is | Deploy as |
|----------|--------|------------|-----------|
| **Service** | `TheKrystalShip.Kgsm.Assistant.Service.dll` | HTTP/SSE turn API (for the web SPA) | systemd, framework-dependent |
| **CLI** | `kgsm-assistant` | Terminal assistant (one-shot / pipe / REPL) | run by a user |
| **RAG indexer** | `kgsm-rag-indexer` | Builds/refreshes the doc vector index | systemd `--watch` (Native-AOT) |

> All sizes/outputs below were produced on the reference host (Arch Linux, .NET 10.0.109,
> Ollama 0.30.10, RTX 3060 12 GB). Treat them as "what good looks like."

---

## 1 · Prerequisites

### 1.1 The kgsm-lib sibling repo (build-time — **do this first**)

`kgsm-llm` does **not** vendor or package `kgsm-lib`. The assistant reaches the kgsm engine
through it via a **project reference to a sibling checkout**:

```
TheKrystalShip.Kgsm.Assistant.Infrastructure.csproj
   → ..\..\kgsm-lib\kgsm-lib\kgsm-lib.csproj
```

So the two repos must sit side by side under one parent directory (the `tks/` workspace
layout). A clone of *only* `kgsm-llm` will fail at `dotnet restore`/build. Everything else
is restored from NuGet (Microsoft.Extensions.* 10.0, ASP.NET 10) — there is **no** custom
feed or `nuget.config`.

```bash
mkdir -p ~/tks && cd ~/tks
git clone <kgsm-lib-url>  kgsm-lib      # the C#↔engine chokepoint (TheKrystalShip.KGSM)
git clone <kgsm-llm-url>  kgsm-llm      # this repo
# Result: ~/tks/kgsm-lib and ~/tks/kgsm-llm as siblings.
```

### 1.2 The .NET 10 SDK

```bash
dotnet --version            # expect 10.0.x
dotnet --list-runtimes | grep -E 'AspNetCore.App 10|NETCore.App 10'   # both needed at runtime
```

The SDK builds everything; the **Service** and **CLI** are published *framework-dependent*,
so the **Microsoft.AspNetCore.App 10** and **Microsoft.NETCore.App 10** runtimes must also be
present on any host that runs them. (The indexer is Native-AOT and needs no runtime.)

### 1.3 Ollama + the two models

```bash
ollama --version                        # reference: 0.30.10
ollama pull gemma4:12b                   # chat model (~8 GB on disk; ~8 GB VRAM at 32k ctx)
ollama pull embeddinggemma               # RAG embedder (~0.7 GB VRAM); only needed if using RAG
```

GPU/VRAM tuning is in [§2](#2--ollama-gpu-tuning). RAG is optional — skip `embeddinggemma`
if you won't enable it.

### 1.4 A kgsm engine on the host (runtime — for the Service & CLI)

The Service and CLI answer questions about, and act on, the game servers that a local **kgsm**
engine manages. They need a real `kgsm.sh` and its instance registry (`~/.local/share/kgsm`)
present at *runtime* (not to build). The RAG indexer does **not** need kgsm at all.

```bash
ls -l /opt/kgsm/kgsm.sh        # or wherever this host's kgsm lives; note the path for KGSM:Path
```

### 1.5 Optional integrations

- **Tavily** (web-search fallback): an API key → `WebSearch__ApiKey`. Without it, `search`
  is local-RAG-only (or omitted entirely if RAG is also off).
- **Discord application** (the Service's auth): a Discord app with a bot in your guild — see
  [§6.2](#62-discord-oauth-secrets). The CLI needs none (your shell user is the trust boundary).

---

## 2 · Ollama GPU tuning

Inference must stay **100% in VRAM** — any spill to CPU/RAM tanks latency and competes with
the game servers. On the reference 12 GB card the layout is `gemma4:12b` ~8 GB (at 32k ctx)
+ `embeddinggemma` ~0.7 GB, with headroom to spare. Set these on the Ollama service
(`/etc/systemd/system/ollama.service.d/override.conf` via `systemctl edit ollama`, or the
environment Ollama starts with):

```ini
[Service]
Environment="OLLAMA_FLASH_ATTENTION=1"     # shrink the KV cache so the big context fits
Environment="OLLAMA_KV_CACHE_TYPE=q8_0"    # ditto (confirmed engaged in server logs)
Environment="OLLAMA_KEEP_ALIVE=-1"         # pin models in VRAM — no cold reloads
Environment="OLLAMA_NUM_PARALLEL=1"        # one request at a time; parallel slots multiply KV (num_ctx × slots)
Environment="OLLAMA_MAX_LOADED_MODELS=2"   # keep chat + embedder both resident (needed for RAG)
```

`num_ctx` is the master VRAM knob and a **fixed reservation**, not a ceiling. The app sets it
per request from `Ollama:NumCtx` (default **32768**); Ollama otherwise silently defaults to
~4096 and truncates.

**Acceptance:**

```bash
ollama ps
# NAME                     ...  PROCESSOR    CONTEXT   UNTIL
# gemma4:12b                    100% GPU     32768     Forever
# embeddinggemma:latest        100% GPU     2048      Forever
```

`PROCESSOR` must read **100% GPU** (never any `% CPU`); `UNTIL: Forever` confirms `KEEP_ALIVE=-1`.

---

## 3 · Build & verify the source

From `~/tks/kgsm-llm`:

```bash
dotnet build  TheKrystalShip.Llm.slnx -c Release      # builds all projects incl. the kgsm-lib sibling
dotnet test   TheKrystalShip.Llm.slnx                 # ~500 hermetic tests (no live deps needed)
```

**Acceptance:** `Build succeeded` (the only warnings are pre-existing XML-doc ones) and
`Passed!` across every test project. The suite is hermetic — it needs no Ollama or kgsm host; the
handful of live smokes early-return (counted as passed, not skipped) unless `KGSM_LIVE_OLLAMA=1`
and a real backend are present. If the build fails resolving `kgsm-lib.csproj`, re-read
[§1.1](#11-the-kgsm-lib-sibling-repo-build-time--do-this-first) — the sibling isn't where the
project reference expects it.

---

## 4 · Publish the artifacts

Three different publish shapes — this is deliberate (see [`ARCHITECTURE.md`](./ARCHITECTURE.md)):

```bash
# Service — framework-dependent (needs the .NET 10 runtime on the host). ~2 MB.
dotnet publish TheKrystalShip.Kgsm.Assistant.Service/TheKrystalShip.Kgsm.Assistant.Service.csproj \
  -c Release -o out/service

# CLI — framework-dependent. ~4 MB. Binary name: kgsm-assistant
dotnet publish TheKrystalShip.Kgsm.Assistant.Cli/TheKrystalShip.Kgsm.Assistant.Cli.csproj \
  -c Release -o out/cli

# RAG indexer — Native-AOT, standalone ELF (no runtime needed). ~7 MB, 0 ILC warnings.
dotnet publish TheKrystalShip.Rag.Indexer/TheKrystalShip.Rag.Indexer.csproj \
  -c Release -r linux-x64 -o out/indexer
```

**Acceptance:** each exits 0; `out/service/TheKrystalShip.Kgsm.Assistant.Service.dll`,
`out/cli/kgsm-assistant`, and `out/indexer/kgsm-rag-indexer` (an `ELF … executable`) exist.
The indexer publish must report **0 IL/ILC warnings** — if it doesn't, an AOT-incompatibility
crept into the RAG core; fix it before shipping (the daemon is the whole reason that core is
AOT-clean).

---

## 5 · The CLI (simplest path — start here)

The CLI is the fastest way to confirm the whole chat→tool→kgsm path works.

```bash
# Point it at this host's kgsm engine and ask something read-only:
KGSM__Path=/opt/kgsm/kgsm.sh  out/cli/kgsm-assistant "How many servers do I have, and what are they?"
```

**Acceptance (reference output):**

```
You have 2 game servers installed:
1. factorio-test
2. terraria-hardmode
```

Exit code `0`. Other entry points: `echo "…" | kgsm-assistant` (pipe), or `kgsm-assistant`
with no args in a TTY (REPL). Full usage, config layering, the `index` verb, and the
`--dump-prompts` tuning surface are in [`../TheKrystalShip.Kgsm.Assistant.Cli/README.md`](../TheKrystalShip.Kgsm.Assistant.Cli/README.md).

To install it for a user, copy `out/cli/` somewhere and symlink the launcher:

```bash
sudo cp -r out/cli /opt/kgsm-assistant/cli
sudo ln -sf /opt/kgsm-assistant/cli/kgsm-assistant /usr/local/bin/kgsm-assistant
```

Set the kgsm path once in the user's config instead of per-invocation — see
[`CONFIGURATION.md`](./CONFIGURATION.md) (`~/.config/kgsm-assistant/appsettings.json`).

> **Gotcha:** the CLI validates `KGSM:Path` at startup and exits `2` if it's missing. If it
> reports "no servers installed" despite servers existing, you've almost certainly overridden
> `XDG_DATA_HOME` (which hides the kgsm registry) — don't. See [§9](#9--troubleshooting).

---

## 6 · The Service (HTTP/SSE API)

A loopback-only ASP.NET minimal-API app. It binds **plain HTTP on 127.0.0.1:5180** and expects
a reverse proxy in front for TLS ([§7](#7--reverse-proxy--tls)). Endpoint, auth-flow, and SSE
details: [`../TheKrystalShip.Kgsm.Assistant.Service/README.md`](../TheKrystalShip.Kgsm.Assistant.Service/README.md).

### 6.1 First boot (no secrets — health only)

The service boots fine without any integration configured; only authenticated turns need them.

```bash
cd out/service
ASPNETCORE_ENVIRONMENT=Production dotnet TheKrystalShip.Kgsm.Assistant.Service.dll &
sleep 2
curl -fsS http://127.0.0.1:5180/health       # -> {"status":"ok"}
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5180/tools   # -> 401 (secured, good)
kill %1
```

**Acceptance:** `{"status":"ok"}`; `/tools` returns `401`; the log lines carry journald `<6>`
priority prefixes (the app uses `AddSystemdConsole()`).

### 6.2 Discord OAuth secrets

The Service authenticates web users with Discord OAuth and decides *who may run actions* from
a guild role. In the Discord Developer Portal: create an app, add a bot, invite it to your
guild, and register your SPA's callback as a redirect URI. Then supply (env-only — never in
appsettings):

| Env var | Purpose | If unset |
|---------|---------|----------|
| `DiscordOAuth__ClientId` | OAuth app id | login can't start |
| `DiscordOAuth__ClientSecret` | code→token exchange (**secret**) | login denied |
| `DiscordOAuth__BotToken` | reads guild membership + roles (**secret**) | **all logins denied** |
| `DiscordOAuth__GuildId` | the server users must belong to | — |
| `DiscordOAuth__ActionRoleId` | role required for mutating actions | everyone read-only |
| `DiscordOAuth__RedirectUri` | must match the portal exactly | callback rejected |
| `Auth__AllowedOrigins__0` | your SPA origin (CORS) | browser calls blocked |
| `Assistant__ActionsEnabled` | master switch for actions | actions off |
| `Assistant__Confirmation__Key` | HMAC signing for confirm tokens — **keep stable** | actions read-only |

Generate a stable signing key once: `openssl rand -base64 48`. If it changes (or is empty),
pending confirmations are rejected and the service falls back to read-only.

### 6.3 Run under systemd

Copy the unit and env template from [`../deploy/`](../deploy/) and follow
[`../deploy/README.md`](../deploy/README.md). The short version:

```bash
sudo install -d /opt/kgsm-assistant/service /etc/kgsm-assistant
sudo cp -r out/service/* /opt/kgsm-assistant/service/
sudo cp deploy/assistant.env.example /etc/kgsm-assistant/service.env
sudo chmod 600 /etc/kgsm-assistant/service.env
sudo "$EDITOR" /etc/kgsm-assistant/service.env          # fill in secrets + KGSM__Path
sudo cp deploy/kgsm-assistant-service.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now kgsm-assistant-service
curl -fsS http://127.0.0.1:5180/health                  # acceptance
```

> **Run the unit as the user that owns the kgsm registry** (the `User=` in the unit). The
> service shells out to kgsm.sh through kgsm-lib; a different user sees zero instances.
> **Sessions are in-memory** — a restart forces every web user to re-login (confirmation
> tokens survive iff the signing key is stable).

---

## 7 · Reverse proxy / TLS

The Service speaks plain HTTP on loopback by design. Terminate TLS at nginx (or Caddy) and
proxy to it. The one non-obvious requirement is **disabling response buffering for the SSE
stream** (`/turn` with `Accept: text/event-stream`) — the app already sends
`X-Accel-Buffering: no`, which nginx honors:

```nginx
location / {
    proxy_pass         http://127.0.0.1:5180;
    proxy_http_version 1.1;
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;

    # SSE: stream tokens through immediately, don't buffer or time out mid-turn.
    proxy_buffering     off;
    proxy_read_timeout  3600s;
    proxy_set_header    Connection '';
}
```

Make sure the TLS hostname here matches `DiscordOAuth__RedirectUri` and is listed in
`Auth__AllowedOrigins`.

---

## 8 · RAG (optional)

RAG lets the assistant answer from your own docs via the `search` tool. It's **off by default**
and a three-part setup: **index the docs → enable retrieval on the consumer → keep the index
fresh**. The producer (indexer) and consumer (Service/CLI) are coupled by exactly one thing:
**the on-disk `.krag` index file**.

### 8.1 Build the index once

```bash
out/indexer/kgsm-rag-indexer --once \
  --source /opt/kgsm-assistant/docs \
  --index  /var/lib/kgsm-assistant/rag-index.krag
```

**Acceptance (reference, indexing the 13-doc sample corpus):**

```
Indexed 13 file(s) → …/rag-index.krag: 13 embedded, 0 reused, 0 removed; 251 chunks (251 newly embedded).
```

The `.krag` is a versioned binary (magic + format version + embedding model + dimension +
chunk params + vectors). Re-runs are **incremental** (unchanged files reused by content hash);
changing the model/dimension/chunk size forces a clean full rebuild automatically (or pass
`--full`).

### 8.2 Enable retrieval on the consumer

Point the Service (and/or CLI) at that same file and switch RAG on:

```bash
# Service env (deploy/assistant.env.example):
Rag__Enabled=true
Rag__IndexPath=/var/lib/kgsm-assistant/rag-index.krag
Rag__EmbeddingModel=embeddinggemma      # MUST match what the indexer used
```

The embedding model must match the one the index was built with — a mismatch is rejected on
load (a different model is a different vector space). The Service then offers `search`; with a
Tavily key set too, it falls back to the web when local hits are weak.

### 8.3 Keep it fresh (the `--watch` daemon)

Install the indexer as a systemd unit so the index rebuilds when docs change:

```bash
sudo install -d /opt/kgsm-assistant/indexer /opt/kgsm-assistant/docs /var/lib/kgsm-assistant
sudo install out/indexer/kgsm-rag-indexer /opt/kgsm-assistant/indexer/
sudo cp deploy/kgsm-rag-indexer.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now kgsm-rag-indexer
```

The daemon debounces bursts of edits, rebuilds incrementally, and **atomically swaps** the
file; the Service **hot-reloads** on the swap (a failed/mid-swap read degrades to the last good
index, never goes dark). The unit is ordered `After=ollama.service` on purpose — see the gap in
[§9](#9--troubleshooting).

Full indexer reference: [`../TheKrystalShip.Rag.Indexer/README.md`](../TheKrystalShip.Rag.Indexer/README.md).

---

## 9 · Troubleshooting & known gaps

| Symptom | Cause & fix |
|---------|-------------|
| **"No servers installed" but they exist** | `XDG_DATA_HOME` was overridden (test rigs do this), hiding kgsm's registry at `~/.local/share/kgsm`. Never set it for the Service/CLI; run the Service as the user that owns the registry. |
| **CLI exits `2` immediately** | `KGSM:Path` missing/wrong. Set `KGSM__Path` or the config key to a real `kgsm.sh`. |
| **Index is stale after a reboot; `journalctl -u kgsm-rag-indexer` shows an embed failure at boot** | **Known gap:** if Ollama is down when the indexer starts, the initial build fails and there is **no periodic retry** — it only rebuilds on the next doc change. Fix: the unit's `After=ollama.service` ordering (already set). If Ollama isn't a systemd unit, fix the ordering or `--once` it manually after Ollama is up. |
| **Service logs "index … model mismatch" / RAG returns nothing** | The `.krag` was built with a different `EmbeddingModel` than `Rag:EmbeddingModel`. Re-index with the configured model (or align the config). |
| **Every web user re-logged-in after a deploy** | Expected — sessions are in-memory. Only confirmation tokens persist, and only if `Assistant__Confirmation__Key` is stable across restarts. |
| **Turns 502 / "couldn't reach the model"** | Ollama down or the model not pulled. `ollama ps` should show the chat model `100% GPU`. |
| **SSE replies arrive all-at-once at the end** | The reverse proxy is buffering. Set `proxy_buffering off` ([§7](#7--reverse-proxy--tls)). |
| **`search` tool never offered** | Both sources are off: `Rag:Enabled=false` *and* no `WebSearch:ApiKey`. Enable at least one. |

### Secrets hygiene

- Supply all secrets **env-only** (the `*.env` file, `chmod 600`). Never put `ClientSecret`,
  `BotToken`, `WebSearch:ApiKey`, or the confirmation key in `appsettings.json`.
- `appsettings.Development.json` (CLI) is **gitignored** and may contain a real local Tavily
  key for dev convenience — it is **not** a deployment template. Don't ship it; use the env file.
- `scripts/tier.env` (gitignored) holds live test Discord creds for `verify-tiers.sh`. Same rule.

---

## 10 · Redeploy / upgrade

```bash
cd ~/tks/kgsm-llm && git pull
cd ~/tks/kgsm-lib && git pull            # keep the sibling in lockstep — it's a source dep
cd ~/tks/kgsm-llm
dotnet test TheKrystalShip.Llm.slnx      # gate the deploy on green
dotnet publish … -o out/service          # re-publish what changed (§4)
sudo cp -r out/service/* /opt/kgsm-assistant/service/
sudo systemctl restart kgsm-assistant-service
curl -fsS http://127.0.0.1:5180/health
```

The RAG index does **not** need rebuilding on a code upgrade unless the on-disk **format
version** changed (the loader rejects an incompatible index loudly — re-run the indexer if so).

---

## 11 · One-screen recap

```
PREREQS   kgsm-lib sibling clone · .NET 10 SDK+runtime · Ollama + gemma4:12b (+ embeddinggemma for RAG) · a kgsm host
BUILD     dotnet test TheKrystalShip.Llm.slnx           → Passed!
PUBLISH   service (FD ~2MB) · cli (FD ~4MB) · indexer (AOT ~7MB, 0 ILC)
CLI       KGSM__Path=… kgsm-assistant "…"               → an answer, exit 0
SERVICE   systemd + reverse proxy + Discord secrets     → curl /health = {"status":"ok"}
RAG (opt) kgsm-rag-indexer --once → .krag · Rag__Enabled=true + same path · --watch daemon to keep fresh
```
