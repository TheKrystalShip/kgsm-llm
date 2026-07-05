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
| **Service** | `kgsm-assistant` | HTTP/SSE turn API (for the web SPA) | systemd, framework-dependent |
| **CLI** | `kgsm-assistant-cli` | Terminal assistant (one-shot / pipe / REPL) | run by a user |
| **RAG indexer** | `kgsm-rag-indexer` | Builds/refreshes the doc vector index | systemd `--watch` (Native-AOT) |

> All sizes/outputs below were produced on the reference host (Arch Linux, .NET 10.0.109,
> Ollama 0.30.10, RTX 3060 12 GB). Treat them as "what good looks like."

---

## 0 · What to change, and where

Everything host-specific lives in **one file**: `/etc/kgsm-assistant/service.env` — the Service's
systemd `EnvironmentFile` (`chmod 600`), seeded from
[`../deploy/assistant.env.example`](../deploy/assistant.env.example). `deploy.sh` creates it from
that template on the first deploy and **never overwrites it** afterward, so your secrets survive
upgrades. Edit it, then `sudo systemctl restart kgsm-assistant-service`. (The CLI reads the same keys
from `~/.config/kgsm-assistant/appsettings.json` or its own environment instead — see §5.)

| Set this | Required? | Notes |
|----------|-----------|-------|
| `KGSM__Path` | **yes** | Absolute path to **this host's** `kgsm.sh`. Without it the assistant has no engine to read or act on. |
| `DiscordOAuth__ClientId` · `ClientSecret` · `BotToken` · `GuildId` · `RedirectUri` | **yes** for web login | From the Discord Developer Portal. `ClientSecret` + `BotToken` are **secrets**; with `BotToken` empty **all logins are denied**. (The CLI needs none of these.) |
| `DiscordOAuth__ActionRoleId` | for actions | Guild role permitted to run mutating actions; everyone else is read-only. |
| `Auth__AllowedOrigins__0` | **yes** for the SPA | Your panel origin (scheme + host, no trailing slash). Empty ⇒ browser calls are CORS-blocked. |
| `Assistant__ActionsEnabled` + `Assistant__Confirmation__Key` | for actions | Set `true` + a **stable** `openssl rand -base64 48`. If the key changes or empties, pending confirmations break and actions fall back to read-only. |
| `Assistant__Relay__Secret` | if fronted by kgsm-api | Shared secret for the trusted-relay hop (kgsm-api → assistant). Empty ⇒ that path is off. **Secret.** |
| `WebSearch__ApiKey` | optional | Tavily key (`tvly-…`, from [tavily.com](https://tavily.com)) — enables the **web** half of the `search` tool. **Secret.** |
| `Rag__Enabled` (template default **true**) + a doc corpus | optional | The **local-doc** half of `search`. On by default — but returns nothing until you populate a corpus and run the indexer (§8) and `ollama pull embeddinggemma`. |
| `Ollama__Model` / `Ollama__Endpoint` | if not default | Defaults: `gemma4:12b` on `localhost:11434`. |
| `ASPNETCORE_URLS` | if not loopback | Default `http://127.0.0.1:5180`; TLS terminates at the reverse proxy (§7). |

> **The `search` tool needs at least one source.** It is offered to the model only when
> `Rag__Enabled` has a **working index** *or* `WebSearch__ApiKey` is set. With both off it is omitted
> entirely — the "assistant has no search tool" state. The shipped template turns RAG on, so pair it
> with a corpus (§8), set a Tavily key, or both.

Every key, its default, and its env-var form: **[`CONFIGURATION.md`](./CONFIGURATION.md)**. Secrets
go in the env file **only** — never in a committed `appsettings.json`.

## 0.1 · Deploy in one command — `deploy/deploy.sh` (recommended)

Once the prerequisites in §1–2 are satisfied, the supported path is the script. It builds, publishes,
installs the systemd units (substituting `User=`/`Group=` to **you**, the invoking user), seeds the
env file from the template if absent, enables the service, and blocks on a real `/health` 200:

```bash
cd ~/tks/kgsm-llm
./deploy/deploy.sh                 # Service + CLI
./deploy/deploy.sh --with-indexer  # also build + enable the RAG indexer (needs Ollama)
```

Run it **as the service user, not root** — it builds as you and `sudo`s only the systemd/root-path
steps. It preflight-checks the .NET 10 ASP.NET runtime and the `kgsm-lib` sibling (§1) and fails fast
if either is missing. On the **first** run it prints a reminder to fill in
`/etc/kgsm-assistant/service.env` (the table above) and restart; on later runs it hot-swaps the
binaries and leaves your env untouched. Non-interactive (CI):
`SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh`.

**The numbered sections below are the manual, step-by-step equivalent** plus the deeper reference
(Ollama tuning, RAG end-to-end, reverse proxy, troubleshooting). Read them to understand — or
customize — what the script automates; you don't need to run them by hand when `deploy.sh` succeeds.

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
ls -l /usr/local/bin/kgsm        # or wherever this host's kgsm lives; note the path for KGSM:Path
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

# CLI — framework-dependent. ~4 MB. Binary name: kgsm-assistant-cli
dotnet publish TheKrystalShip.Kgsm.Assistant.Cli/TheKrystalShip.Kgsm.Assistant.Cli.csproj \
  -c Release -o out/cli

# RAG indexer — Native-AOT, standalone ELF (no runtime needed). ~7 MB, 0 ILC warnings.
dotnet publish TheKrystalShip.Rag.Indexer/TheKrystalShip.Rag.Indexer.csproj \
  -c Release -r linux-x64 -o out/indexer
```

**Acceptance:** each exits 0; `out/service/kgsm-assistant.dll`,
`out/cli/kgsm-assistant-cli`, and `out/indexer/kgsm-rag-indexer` (an `ELF … executable`) exist.
The indexer publish must report **0 IL/ILC warnings** — if it doesn't, an AOT-incompatibility
crept into the RAG core; fix it before shipping (the daemon is the whole reason that core is
AOT-clean).

---

## 5 · The CLI (simplest path — start here)

The CLI is the fastest way to confirm the whole chat→tool→kgsm path works.

```bash
# Point it at this host's kgsm engine and ask something read-only:
KGSM__Path=/usr/local/bin/kgsm  out/cli/kgsm-assistant-cli "How many servers do I have, and what are they?"
```

**Acceptance (reference output):**

```
You have 2 game servers installed:
1. factorio-test
2. terraria-hardmode
```

Exit code `0`. Other entry points: `echo "…" | kgsm-assistant-cli` (pipe), or `kgsm-assistant-cli`
with no args in a TTY (REPL). Full usage, config layering, the `index` verb, and the
`--dump-prompts` tuning surface are in [`../TheKrystalShip.Kgsm.Assistant.Cli/README.md`](../TheKrystalShip.Kgsm.Assistant.Cli/README.md).

To install it for a user, copy `out/cli/` somewhere and symlink the launcher:

```bash
sudo cp -r out/cli /opt/kgsm-assistant/cli
sudo ln -sf /opt/kgsm-assistant/cli/kgsm-assistant-cli /usr/local/bin/kgsm-assistant-cli
```

Set the kgsm path once in the user's config instead of per-invocation — see
[`CONFIGURATION.md`](./CONFIGURATION.md) (`~/.config/kgsm-assistant/appsettings.json`).

> **Gotcha:** the CLI validates `KGSM:Path` at startup and exits `2` if it's missing. If it
> reports "no servers installed" despite servers existing, you've almost certainly overridden
> `XDG_DATA_HOME` (which hides the kgsm registry) — don't. See [§9](#9--troubleshooting--known-gaps).

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

`deploy/deploy.sh` ([§0.1](#01--deploy-in-one-command--deploydeploysh-recommended)) does all of
this for you; the manual equivalent (copy the unit + env template from [`../deploy/`](../deploy/),
also covered in [`../deploy/README.md`](../deploy/README.md)) is:

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

## 8 · RAG — local-doc search

RAG lets the assistant answer from **your own docs** via the `search` tool. The shipped env template
**enables it by default** (`Rag__Enabled=true`); the embedded `appsettings.json` baseline stays
`false`, so a consumer with no env override is off. It's a three-part setup — **index the docs →
enable retrieval on the consumer → keep the index fresh** — coupled by exactly one thing: the on-disk
`.krag` index file (the indexer writes it; the Service/CLI read it).

> **Enabled ≠ working.** With `Rag__Enabled=true` but no index yet, retrieval just returns nothing (it
> never errors) and `search` falls back to the web. To get *local* hits you must (a) `ollama pull
> embeddinggemma`, (b) put docs where the indexer looks, and (c) run the indexer. On a host that won't
> run the indexer, either set `Rag__Enabled=false` there or rely on a Tavily key alone — otherwise
> `search` is offered but its local half is always empty.

### 8.1 Build the index once

`/opt/kgsm-assistant/docs` is the conventional corpus dir, but it is **empty on a fresh deploy** —
populate it first, or the index comes out empty. Drop your `.md` files there, symlink a docs tree into
it, or just point `--source` at any directory or files (it walks dirs recursively, honors
`Rag__SourcePattern`, and accepts multiple `--source` flags — e.g. `--source ~/tks` to index the whole
workspace). The indexer needs neither kgsm nor the Service — only Ollama for the embeddings.

```bash
out/indexer/kgsm-rag-indexer --once \
  --source /opt/kgsm-assistant/docs \
  --index  /var/lib/kgsm-assistant/rag-index.krag
```

**Acceptance (reference, indexing a 13-doc corpus):**

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

The shipped unit watches `--source /opt/kgsm-assistant/docs` (still empty until you fill it — §8.1);
edit that line, or add more `--source` flags, to index a different tree, and keep the unit's `--index`
equal to the Service's `Rag__IndexPath` (that one file is the whole producer→consumer contract). The
daemon debounces bursts of edits, rebuilds incrementally, and **atomically swaps** the file; the
Service **hot-reloads** on the swap (a failed/mid-swap read degrades to the last good index, never
goes dark). The unit is ordered `After=ollama.service` on purpose — see the gap in
[§9](#9--troubleshooting--known-gaps).

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
| **`search` runs but never cites a local doc** | RAG is enabled but its local half is empty: no index yet, or the indexer's `--source` corpus is empty (`/opt/kgsm-assistant/docs` is unpopulated on a fresh deploy). Populate the corpus + run the indexer ([§8.1](#81-build-the-index-once)); until then it correctly falls back to the web. |

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
DEPLOY    ./deploy/deploy.sh [--with-indexer]           → builds, installs units, /health = 200 (the supported path)
CONFIG    edit /etc/kgsm-assistant/service.env (§0)     → KGSM__Path + Discord + CORS (+ Tavily/RAG) · restart
BUILD     dotnet test TheKrystalShip.Llm.slnx           → Passed!   (what deploy.sh gates on)
PUBLISH   service (FD ~2MB) · cli (FD ~4MB) · indexer (AOT ~7MB, 0 ILC)
CLI       KGSM__Path=… kgsm-assistant-cli "…"               → an answer, exit 0
SERVICE   systemd + reverse proxy + Discord secrets     → curl /health = {"status":"ok"}
RAG (opt) populate corpus → kgsm-rag-indexer --once → .krag · Rag__Enabled=true (template default) + same path · --watch to keep fresh
```
