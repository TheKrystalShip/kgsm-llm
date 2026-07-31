# Configuration Reference — kgsm-llm

Every config section the assistant reads, its keys, defaults, and environment-variable form.
For *how to deploy* see [`DEPLOYMENT.md`](./DEPLOYMENT.md); for *how the pieces fit* see
[`ARCHITECTURE.md`](./ARCHITECTURE.md).

## How configuration is layered

All three deployables use the standard .NET configuration stack. Later layers win:

1. **Embedded `appsettings.json`** — shipped next to each binary; the source of truth for defaults.
2. **A user/host config file** —
   - **CLI:** `$KGSM_ASSISTANT_CONFIG`, else `--config <path>`, else `~/.config/kgsm-assistant/appsettings.json` (`$XDG_CONFIG_HOME` honored).
   - **Service:** `appsettings.{ASPNETCORE_ENVIRONMENT}.json` next to the binary (e.g. `appsettings.Production.json`).
3. **Environment variables** — `Section__Key` form (see below). This is where **secrets** belong.
4. **CLI flags** (CLI only) — e.g. `--model` wins for the model tag.

### Environment-variable form

Replace each `:` in a config path with a double underscore `__`:

```bash
Ollama__Model=mistral:7b          # Ollama:Model
Assistant__Confirmation__Key=…    # Assistant:Confirmation:Key  (nested)
Rag__Enabled=true                 # Rag:Enabled
```

**Arrays** use a numeric index segment:

```bash
Auth__AllowedOrigins__0=https://panel.example.com
Auth__AllowedOrigins__1=https://staging.example.com
Rag__Sources__0=/opt/kgsm-assistant/docs
```

### Secrets — environment-only, never in a file

These must come from the environment (the systemd `EnvironmentFile`, `chmod 600`), never from
any committed `appsettings.json`:

| Secret | Used by |
|--------|---------|
| `DiscordOAuth__ClientSecret` | Service |
| `DiscordOAuth__BotToken` | Service |
| `Assistant__Confirmation__Key` | Service (keep **stable** across restarts) |
| `Assistant__Webhook__Secret` | Service |
| `Assistant__Relay__Secret` | Service (optional) |
| `WebSearch__ApiKey` | Service & CLI |

> An **empty** secret means "disabled," not "error": no `WebSearch:ApiKey` ⇒ web search off (and the
> `search` tool omitted entirely only if `Rag:Enabled` is also off); no `Assistant:Confirmation:Key` ⇒
> actions fall back to read-only; no `Assistant:Webhook:Secret` ⇒ webhook signatures unverified (dev only).

---

## Which sections apply to which deployable

| Section | CLI | Service | Indexer | Purpose |
|---------|:---:|:-------:|:-------:|---------|
| `Ollama` | ✅ | ✅ | — | Chat model client |
| `Conversation` | ✅ | ✅ | — | Per-turn short-term memory |
| `LlmAgent` | ✅ | ✅ | — | Agent-loop safety caps |
| `Recording` | ✅ (on) | ✅ (off) | — | Transcript corpus (opt-in) |
| `KGSM` | ✅ | ✅ | — | Path to `kgsm.sh` |
| `InventoryCache` | ✅ | ✅ | — | Instance/blueprint cache TTLs |
| `Monitor` | ✅ | ✅ | — | kgsm-monitor metrics socket (`get_performance`) |
| `WebSearch` | ✅ | ✅ | — | Tavily fallback |
| `WebFetch` | ✅ | ✅ | — | Direct URL fetch (`fetch_url`) |
| `BlueprintAuthoring` | ✅ | ✅ | — | Autonomous catalog authoring (`create_blueprint`) |
| `Rag` (retrieval) | ✅ | ✅ | — | Local doc retrieval (consumer) |
| `Rag` (embedder + build) | ✅ (`index` verb) | reads only | ✅ | Embedding model + chunking |
| `Prompts` | ✅ | (built-in) | — | Editable persona/tool prompts |
| `Assistant` | — | ✅ | — | Action policy, confirm/webhook/relay |
| `DiscordOAuth` | — | ✅ | — | Web auth |
| `Auth` | — | ✅ | — | Sessions + CORS |
| `Urls` / `Logging` | (Logging) | ✅ | (Logging) | Bind address / log levels |

The **indexer takes CLI flags, not a config file** — its row maps to `--model`, `--endpoint`,
`--source`, `--index`, `--chunk-size`, etc. (see [its README](../TheKrystalShip.Rag.Indexer/README.md)).

---

## Sections

### `Ollama` — chat model client (`OllamaOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Endpoint` | `http://localhost:11434` | `Ollama__Endpoint` | Ollama base URL |
| `Model` | `gemma4:12b` | `Ollama__Model` | Chat model tag (CLI `--model` overrides) |
| `NumCtx` | `32768` | `Ollama__NumCtx` | Context window — a **fixed VRAM reservation** |
| `TimeoutSeconds` | `300` | `Ollama__TimeoutSeconds` | Per-request generation timeout |
| `Temperature` | `0.3` | `Ollama__Temperature` | Low keeps tool routing reliable |
| `Seed` | _(unset)_ | `Ollama__Seed` | Reproducible sampling (eval/testing) |
| `Think` | `true` (CLI) | `Ollama__Think` | Gemma "thinking" mode; CLI `--think/--no-think` overrides |

### `Conversation` — short-term memory (`ConversationOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `MaxMessages` | `12` | `Conversation__MaxMessages` | Rolling window; older messages dropped |
| `IdleTimeoutMinutes` | `15` | `Conversation__IdleTimeoutMinutes` | Inactivity before a conversation resets |

### `LlmAgent` — agent loop (`LlmAgentOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `MaxIterations` | `8` | `LlmAgent__MaxIterations` | Cap on model↔tool round-trips per turn |
| `MaxToolOutputChars` | `1500` | `LlmAgent__MaxToolOutputChars` | Tool output truncated before feeding back |
| `IterationLimitReply` | _(built-in)_ | `LlmAgent__IterationLimitReply` | Fallback reply when the cap is hit |

### `Recording` — transcript corpus (`RecordingOptions`)

Append-only JSONL of turns, for prompt-tuning/eval. **On by default in the CLI**, **off in the Service**.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Enabled` | `true` (CLI) / `false` (Service) | `Recording__Enabled` | Master switch |
| `Directory` | _(CLI: XDG data home)_ | `Recording__Directory` | Daily `yyyy-MM-dd.jsonl` (CLI: `~/.local/share/kgsm-assistant/transcripts/`) |
| `Label` | _(empty)_ | `Recording__Label` | Stamp for A/B-ing prompt edits (CLI `--label`) |

### `KGSM` — engine location (`KgsmConnectionOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Path` | `/usr/local/bin/kgsm` | `KGSM__Path` | Path to `kgsm.sh`. The CLI **validates this at startup** (exits `2` if missing) |
| `EventSocketPath` | _(empty)_ | `KGSM__EventSocketPath` | Unix socket this process **binds** to receive kgsm's engine events; a blueprint write invalidates the blueprint cache. Empty = bind nothing. The service unit sets `/run/kgsm-assistant/events.sock`; the CLI leaves it empty (binding is exclusive). kgsm delivers only to paths listed in its own `event_socket_filenames` |

### `InventoryCache` — kgsm read cache (`InventoryCacheOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `InstancesTtlSeconds` | `300` | `InventoryCache__InstancesTtlSeconds` | Instance-list cache TTL (backstop; the webhook invalidates) |
| `BlueprintsTtlSeconds` | `600` | `InventoryCache__BlueprintsTtlSeconds` | Blueprint-list cache TTL (backstop; a `blueprint_*` event invalidates) |

### `Monitor` — kgsm-monitor metrics socket (`MonitorOptions`)

Where the `get_performance` tool scrapes live per-server metrics. The monitor serves its latest frame
over an unauthenticated, pull-only `GET /metrics` on a unix-domain socket. Optional — with no monitor
reachable the tool reports the monitor unavailable (an honest "couldn't read", never a fabricated
number), so this is a path, not a dependency.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `SocketPath` | `/run/kgsm-monitor/metrics.sock` | `Monitor__SocketPath` | Path to the monitor's metrics unix socket |

### `WebSearch` — Tavily fallback (`WebSearchOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ApiKey` | _(empty)_ | `WebSearch__ApiKey` | **Secret.** Empty ⇒ web search disabled |
| `MaxResults` | `4` | `WebSearch__MaxResults` | Results per search |
| `SearchDepth` | `basic` | `WebSearch__SearchDepth` | `basic` (1 credit) or `advanced` (2) |
| `TimeoutSeconds` | `10` | `WebSearch__TimeoutSeconds` | Per-search timeout |
| `MaxCallsPerDay` | `200` | `WebSearch__MaxCallsPerDay` | Daily spend backstop |

### `WebFetch` — direct URL fetch, `fetch_url` (`WebFetchOptions`)

Reads ONE specific page (a doc, a Steam page, a raw Dockerfile) — distinct from `WebSearch`, which only
returns provider-summarized hits. Needs **no API key** (it's a direct GET); `Enabled` alone gates it,
independently of `WebSearch:ApiKey`. This host is internet-exposed and the URL is model/user-influenced,
so the adapter enforces an SSRF guard (rejects loopback/private/link-local/multicast/reserved addresses,
including the `169.254.169.254` cloud-metadata address, re-validated on every redirect hop — auto-redirect
is off and the adapter follows manually) on top of these config knobs.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Enabled` | `false` | `WebFetch__Enabled` | Master switch; false ⇒ `fetch_url` fails closed and is omitted from the tool catalog |
| `TimeoutSeconds` | `8` | `WebFetch__TimeoutSeconds` | Per-fetch timeout (connect + read) |
| `MaxContentBytes` | `3145728` (3 MiB) | `WebFetch__MaxContentBytes` | Hard cap on bytes read from the body; fetching stops and the result is marked truncated |
| `MaxRedirects` | `5` | `WebFetch__MaxRedirects` | Redirect hops followed (each re-validated by the SSRF guard) before giving up |
| `MaxCallsPerDay` | `200` | `WebFetch__MaxCallsPerDay` | Daily spend backstop, mirrors `WebSearch:MaxCallsPerDay` |
| `AllowedHosts` | `[]` | `WebFetch__AllowedHosts__0`, … | Optional operator allowlist (exact host or subdomain match); empty ⇒ no restriction beyond the SSRF guard |
| `DeniedHosts` | `[]` | `WebFetch__DeniedHosts__0`, … | Optional operator denylist, checked before the allowlist |

### `BlueprintAuthoring` — autonomous catalog authoring, `create_blueprint` (`BlueprintAuthoringOptions`)

Given a game missing from the catalog, researches it (via `search`/`fetch_url`), drafts a native-Linux
blueprint, test-installs it on the host to prove it boots and listens, tears the test instance down, and
keeps the blueprint only if verified. `Enabled` alone gates it — false (the default) means `create_blueprint`
is never offered and, even if a client somehow requested it, the pipeline returns an honest "not
configured" without touching kgsm-lib's write-side blueprint/instance authorities at all.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Enabled` | `false` | `BlueprintAuthoring__Enabled` | Master switch; false ⇒ `create_blueprint` fails closed and is omitted from the tool catalog |
| `StashDir` | `""` | `BlueprintAuthoring__StashDir` | Where a failed/infeasible attempt's draft + provenance + verify log are recorded for admin review; empty ⇒ records are dropped rather than written |
| `MaxAttempts` | `2` | `BlueprintAuthoring__MaxAttempts` | Bound on the persist→install→verify retry loop before giving up |
| `VerifyTimeoutSeconds` | `180` | `BlueprintAuthoring__VerifyTimeoutSeconds` | How long to poll a test-install for "booted and listening" before giving up on that attempt |
| `VerifyPollIntervalSeconds` | `5` | `BlueprintAuthoring__VerifyPollIntervalSeconds` | Interval between verify polls |

Needs `WebSearch` and/or `WebFetch` configured to research anything — with neither wired the pipeline
still runs (it's not a separate gate) but the research step honestly finds nothing to work from and the
tool reports it couldn't do the game. The test-install runs under a reserved
`__bp_probe_<name>__` instance name and is always torn down before the tool returns; the Service host also
runs a one-shot startup sweep that removes any such probe a prior crash left behind.

### `Rag` — retrieval, embedder, and index build (one section, three consumers)

The retrieval half (consumer) is read by the CLI & Service; the embedder/build half is read by
the CLI `index` verb (the standalone indexer takes the same values as CLI flags). The embedded
`appsettings.json` baseline is **off**, but the shipped Service env template
(`deploy/assistant.env.example`) sets `Rag__Enabled=true`, so a default deploy is **on** — see the
"enabled ≠ working" caveat in [DEPLOYMENT.md §8](./DEPLOYMENT.md#8--rag--local-doc-search).

**Retrieval (consumer):**

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Enabled` | `false` baseline / **`true`** in the deploy env template | `Rag__Enabled` | Master switch; false ⇒ retrieval fails closed, `search` omitted unless web is on. On ⇒ `search` is offered but its local half is empty until an index exists |
| `IndexPath` | _(empty)_ | `Rag__IndexPath` | Path to the `.krag` file the indexer writes; missing file is fine until it runs |
| `TopK` | `5` | `Rag__TopK` | Chunks retrieved per query |
| `MinScore` | `0.0` | `Rag__MinScore` | Cosine floor at retrieval (kept permissive; the aggregator decides) |
| `LocalMinScore` | `0.35` | `Rag__LocalMinScore` | At/above this, a local hit answers without a web call (`SearchOptions`) |
| `MaxContextChars` | `6000` | `Rag__MaxContextChars` | Cap on grounding injected into the prompt (`SearchOptions`) |

**Embedder (`RagEmbeddingOptions`) + build settings:**

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `EmbeddingModel` | `embeddinggemma` | `Rag__EmbeddingModel` | **Must match the model the index was built with** (mismatch rejected on load) |
| `Endpoint` | `http://localhost:11434` | `Rag__Endpoint` | Ollama URL for embeds (may differ from chat) |
| `TimeoutSeconds` | `120` | `Rag__TimeoutSeconds` | Embed request timeout |
| `Sources` | `[]` | `Rag__Sources__0…` | Docs to index (files/dirs, recursive) |
| `SourcePattern` | `*.md` | `Rag__SourcePattern` | Glob when walking directories |
| `ChunkSize` | `2000` | `Rag__ChunkSize` | Chunk target (chars); changing forces a full rebuild |
| `ChunkOverlap` | `200` | `Rag__ChunkOverlap` | Chunk overlap (chars) |

### `Prompts` — editable persona/tool text (CLI)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Directory` | _(CLI: XDG config home)_ | `Prompts__Directory` | `preamble.md`, `actions-*.md`, `tools.json`; seed with `kgsm-assistant-cli --dump-prompts`; re-read every turn |

---

## Service-only sections

### `Urls` / `ASPNETCORE_URLS`

Bind address. Default `http://localhost:5180` (loopback). Override with the standard
`ASPNETCORE_URLS` env var (e.g. `http://127.0.0.1:5180`). TLS terminates at a reverse proxy.

### `Assistant` — action policy (`AssistantServiceOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ActionsEnabled` | `false` | `Assistant__ActionsEnabled` | Master switch for mutating actions |
| `Confirmation:Key` | _(empty)_ | `Assistant__Confirmation__Key` | **Secret.** HMAC for confirm tokens — **keep stable** or pending confirms break |
| `Confirmation:TtlSeconds` | `300` | `Assistant__Confirmation__TtlSeconds` | Confirmation token lifetime |
| `Webhook:Secret` | _(empty)_ | `Assistant__Webhook__Secret` | **Secret.** HMAC for `POST /events`; empty ⇒ unverified (dev) |
| `Relay:Secret` | _(empty)_ | `Assistant__Relay__Secret` | **Secret.** Trusted-relay auth (e.g. kgsm-api); empty ⇒ relay path off |

### `DiscordOAuth` — web auth (`DiscordOAuthOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ClientId` | _(empty)_ | `DiscordOAuth__ClientId` | OAuth app id |
| `ClientSecret` | _(empty)_ | `DiscordOAuth__ClientSecret` | **Secret.** code→token exchange |
| `BotToken` | _(empty)_ | `DiscordOAuth__BotToken` | **Secret.** Reads roles; unset ⇒ **all logins denied** |
| `GuildId` | _(empty)_ | `DiscordOAuth__GuildId` | Server users must belong to |
| `ActionRoleId` | _(empty)_ | `DiscordOAuth__ActionRoleId` | Role required for mutating actions |
| `RedirectUri` | _(empty)_ | `DiscordOAuth__RedirectUri` | Must match the Developer Portal exactly |
| `Scopes` | `identify` | `DiscordOAuth__Scopes` | Roles are read via the bot, not the caller token |

### `Auth` — sessions & CORS (`AuthOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `SessionTtlSeconds` | `3600` | `Auth__SessionTtlSeconds` | Bearer-token lifetime (in-memory; lost on restart) |
| `RoleCacheTtlSeconds` | `60` | `Auth__RoleCacheTtlSeconds` | Per-user authority cache |
| `StateTtlSeconds` | `300` | `Auth__StateTtlSeconds` | OAuth `state` lifetime |
| `AllowedOrigins` | `[]` | `Auth__AllowedOrigins__0…` | CORS origins (scheme+host, no trailing slash); empty ⇒ SPA blocked |

---

## `Logging`

Standard .NET logging. The Service uses `AddSystemdConsole()` (journald `<N>` priority
prefixes); the CLI/indexer use a `SimpleConsole`→stderr variant (quiet by default; `--verbose`
or `Logging:LogLevel:Default=Debug` to raise).

| Key | Default | Env |
|-----|---------|-----|
| `Logging:LogLevel:Default` | `Information` (Service) / `Warning` (CLI) | `Logging__LogLevel__Default` |
| `Logging:LogLevel:Microsoft.AspNetCore` | `Warning` | `Logging__LogLevel__Microsoft.AspNetCore` |
| `Logging:LogLevel:TheKrystalShip` | `Debug` | `Logging__LogLevel__TheKrystalShip` |

---

## Minimal configurations

**CLI, no RAG, no web search** — just `KGSM__Path` and a reachable Ollama:

```bash
KGSM__Path=/usr/local/bin/kgsm kgsm-assistant-cli "is factorio running?"
```

**Service, full** — see [`../deploy/assistant.env.example`](../deploy/assistant.env.example) for
the complete env file.

**Indexer** — config-free; everything is a flag:

```bash
kgsm-rag-indexer --once --source /opt/kgsm-assistant/docs --index /var/lib/kgsm-assistant/rag-index.krag
```
