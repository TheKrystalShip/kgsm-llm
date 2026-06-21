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

> An **empty** secret means "disabled," not "error": no `WebSearch:ApiKey` ⇒ web search off and
> the tool omitted; no `Assistant:Confirmation:Key` ⇒ actions fall back to read-only; no
> `Assistant:Webhook:Secret` ⇒ webhook signatures unverified (dev only).

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
| `WebSearch` | ✅ | ✅ | — | Tavily fallback |
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
| `Path` | `/opt/kgsm/kgsm.sh` | `KGSM__Path` | Path to `kgsm.sh`. The CLI **validates this at startup** (exits `2` if missing) |

### `InventoryCache` — kgsm read cache (`InventoryCacheOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `InstancesTtlSeconds` | `300` | `InventoryCache__InstancesTtlSeconds` | Instance-list cache TTL (backstop; the webhook invalidates) |
| `BlueprintsTtlSeconds` | `600` | `InventoryCache__BlueprintsTtlSeconds` | Blueprint-list cache TTL |

### `WebSearch` — Tavily fallback (`WebSearchOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ApiKey` | _(empty)_ | `WebSearch__ApiKey` | **Secret.** Empty ⇒ web search disabled |
| `MaxResults` | `4` | `WebSearch__MaxResults` | Results per search |
| `SearchDepth` | `basic` | `WebSearch__SearchDepth` | `basic` (1 credit) or `advanced` (2) |
| `TimeoutSeconds` | `10` | `WebSearch__TimeoutSeconds` | Per-search timeout |
| `MaxCallsPerDay` | `200` | `WebSearch__MaxCallsPerDay` | Daily spend backstop |

### `Rag` — retrieval, embedder, and index build (one section, three consumers)

The retrieval half (consumer) is read by the CLI & Service; the embedder/build half is read by
the CLI `index` verb (the standalone indexer takes the same values as CLI flags). Off by default.

**Retrieval (consumer):**

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Enabled` | `false` | `Rag__Enabled` | Master switch; false ⇒ retrieval fails closed, `search` omitted unless web is on |
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
| `Directory` | _(CLI: XDG config home)_ | `Prompts__Directory` | `preamble.md`, `actions-*.md`, `tools.json`; seed with `kgsm-assistant --dump-prompts`; re-read every turn |

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
KGSM__Path=/opt/kgsm/kgsm.sh kgsm-assistant "is factorio running?"
```

**Service, full** — see [`../deploy/assistant.env.example`](../deploy/assistant.env.example) for
the complete env file.

**Indexer** — config-free; everything is a flag:

```bash
kgsm-rag-indexer --once --source /opt/kgsm-assistant/docs --index /var/lib/kgsm-assistant/rag-index.krag
```
