# Configuration Reference — kgsm-llm

Every config section the assistant reads, its keys, defaults, and environment-variable form.
For *how to deploy* see [`DEPLOYMENT.md`](./DEPLOYMENT.md); for *how the pieces fit* see
[`ARCHITECTURE.md`](./ARCHITECTURE.md).

## How configuration is layered

All three deployables use the standard .NET configuration stack. Later layers win:

1. **The settings file next to the binary** — the **Service** ships `kgsm-assistant.settings.json`,
   which declares its whole configurable surface with defaults; the CLI and Eval ship
   `appsettings.json`. A key not declared there binds to nothing, whatever sets it.
2. **A user/host config file** —
   - **CLI:** `$KGSM_ASSISTANT_CONFIG`, else `--config <path>`, else `~/.config/kgsm-assistant/appsettings.json` (`$XDG_CONFIG_HOME` honored).
   - **Service:** `kgsm-assistant.settings.{ASPNETCORE_ENVIRONMENT}.json` next to the binary.
3. **Environment variables** — `Section__Key` form (see below). This is where **secrets** belong.
4. **CLI flags** (CLI only) — e.g. `--model` wins for the model tag.

### Environment-variable form

Replace each `:` in a config path with a double underscore `__`:

```bash
Llm__Model=mistral:7b             # Llm:Model
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
any committed settings file — which declares each of them blank so the Control Panel can see it exists:

| Secret | Used by |
|--------|---------|
| `KgsmAuth__Providers__discord__ClientSecret` | Service (shared, `/etc/kgsm/kgsm-auth.env`) |
| `Auth__SigningKey` | Service (optional — blank means the host generates and keeps its own) |
| `Assistant__Webhook__Secret` | Service |
| `Assistant__Relay__Secret` | Service (optional) |
| `WebSearch__ApiKey` | Service & CLI |

> An **empty** secret means "disabled," not "error": no `WebSearch:ApiKey` ⇒ web search off (and the
> `search` tool omitted entirely only if `Rag:Enabled` is also off); no `Assistant:Webhook:Secret` ⇒
> webhook signatures unverified (dev only).

---

## Which sections apply to which deployable

| Section | CLI | Service | Indexer | Purpose |
|---------|:---:|:-------:|:-------:|---------|
| `Llm` | ✅ | ✅ | — | Chat model client and which server serves it |
| `Conversation` | ✅ | ✅ | — | Per-turn short-term memory |
| `Memory` | ✅ | ✅ | — | What outlasts a conversation, per owner |
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
| `Prompts` | ✅ | ✅ | — | The persona + tool definitions, on disk. **Required** |
| `Assistant` | — | ✅ | — | Action policy, confirm/webhook/relay |
| `KgsmAuth` | — | ✅ | — | The host's Discord application (shared) |
| `DiscordOAuth` | — | ✅ | — | This surface's sign-in callback |
| `Auth` | — | ✅ | — | Sessions + CORS |
| `Urls` / `Logging` | (Logging) | ✅ | (Logging) | Bind address / log levels |

The **indexer takes CLI flags, not a config file** — its row maps to `--model`, `--endpoint`,
`--source`, `--index`, `--chunk-size`, etc. (see [its README](../TheKrystalShip.Rag.Indexer/README.md)).

---

## Sections

### `Llm` — chat model client (`LlmBackendOptions`)

`Provider` picks which local inference server answers. It is the only key that differs between
them; every other key here means the same thing either way, and nothing above `ILlmClient` sees the
choice at all.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Provider` | `Ollama` | `Llm__Provider` | `Ollama` or `LlamaCpp`. Read once at startup — a swap is a restart |
| `Endpoint` | `http://localhost:11434` | `Llm__Endpoint` | Base URL of the inference server |
| `Model` | `gemma4:12b` | `Llm__Model` | Ollama resolves it as a pulled tag; llama-server only echoes it back (CLI `--model` overrides) |
| `ContextWindow` | `32768` | `Llm__ContextWindow` | A **fixed VRAM reservation**. Ollama takes it per request; llama-server fixes it at launch (`-c`) and this must match that flag |
| `TimeoutSeconds` | `300` | `Llm__TimeoutSeconds` | Per-request generation timeout |
| `Temperature` | `0.3` | `Llm__Temperature` | Low keeps tool routing reliable |
| `Seed` | _(unset)_ | `Llm__Seed` | Reproducible sampling (eval/testing) |
| `Think` | `true` (CLI) | `Llm__Think` | Reasoning mode, on models that support it; CLI `--think/--no-think` overrides |

**`Llm:LlamaCpp`** — read only when `Provider` is `LlamaCpp` (`LlamaCppOptions`):

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ApiKey` | _(blank)_ | `Llm__LlamaCpp__ApiKey` | Sent as a bearer token when llama-server was started with `--api-key`. Blank sends no header |
| `ParallelToolCalls` | `false` | `Llm__LlamaCpp__ParallelToolCalls` | Lets one step request several tools. Off matches how the assistant is prompted and measured |
| `ThinkingTemplateKwarg` | `enable_thinking` | `Llm__LlamaCpp__ThinkingTemplateKwarg` | The chat-template variable `Think` sets, sent on every request in both states. `--reasoning auto` enables reasoning when the request says nothing, so "off" has to be said. A template declaring no such variable ignores it |
| `DryMultiplier` | `0.8` | `Llm__LlamaCpp__DryMultiplier` | DRY sampling strength — the backstop against a repetition loop that generates until the context is full and answers nothing. `0` disables it, leaving a loop bounded only by the context window |
| `DryBase` | `1.75` | `Llm__LlamaCpp__DryBase` | How steeply the DRY penalty grows as a repeated sequence gets longer |
| `DryAllowedLength` | `4` | `Llm__LlamaCpp__DryAllowedLength` | How long a verbatim repeat may run before DRY penalises extending it. Four clears the short repeats structured output is full of |
| `DryPenaltyLastN` | `1024` | `Llm__LlamaCpp__DryPenaltyLastN` | How far back DRY looks for a repeat; `-1` scans the whole context |

> llama-server must be started with `--jinja` and a tools-capable chat template. Without it the
> `tools` array is accepted and no tool call is ever emitted — the assistant answers and never acts.
> Units and setup: [`deploy/llama-server/README.md`](../deploy/llama-server/README.md).

### `Conversation` — the history and what the model replays (`ConversationOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `DatabasePath` | beside the binary | `Conversation__DatabasePath` | SQLite file holding the whole corpus; the Service points it at its state dir |
| `CompactAtPercent` | `70` | `Conversation__CompactAtPercent` | Context occupancy at which a finished turn is summarised into a checkpoint. `0` leaves compaction manual |

The history is **append-only and never trimmed** — there is no rolling window and no idle timeout, and
deliberately no knob for either. What bounds the model's context is a **checkpoint**: compaction folds
the turns so far into a summary and the model replays that summary plus everything after it, while the
transcript keeps every word. A checkpoint carrying *no* summary is a **reset** — the conversation
continues from nothing, which is what `/new` does in a shared room, where the id is derived from the
place and there is no second id to move to.

`CompactAtPercent` is well under 100 on purpose: it is measured on the turn that just finished, and the
next one still needs room for a fresh system prompt, the injected server lists, and its tool output.

### `Memory` — what outlasts a conversation (`MemoryOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `MaxPerOwner` | `64` | `Memory__MaxPerOwner` | Most memories one owner may hold. A write past it is refused naming the cap, never evicted |
| `MaxSummaryLength` | `200` | `Memory__MaxSummaryLength` | Longest one-line summary — the line injected into every turn |
| `MaxBodyLength` | `2000` | `Memory__MaxBodyLength` | Longest body, read only on demand |

Memories live in the **same SQLite file** as the conversation history
(`Conversation:DatabasePath`), so there is deliberately no path of their own: one file is this
assistant's whole durable state, and a second path would let the two halves land on different disks.

A memory belongs to an **owner** — the conversation id up to its second `:` — so `web:{user}:{chat}`
resolves to `web:{user}` and crosses that person's chats, while `room:{room}` owns its own. Every
limit here is a **context** budget rather than a storage one: each memory costs a line in every system
prompt built for that owner, which is why the cap refuses rather than evicting.

### `LlmAgent` — agent loop (`LlmAgentOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `MaxIterations` | `8` | `LlmAgent__MaxIterations` | Cap on model↔tool round-trips per turn |
| `MaxToolOutputChars` | `1500` | `LlmAgent__MaxToolOutputChars` | Tool output truncated before feeding back |
| `IterationLimitReply` | _(built-in)_ | `LlmAgent__IterationLimitReply` | Fallback reply when the cap is hit |
| `EmptyReplyReply` | _(built-in)_ | `LlmAgent__EmptyReplyReply` | Reply when the model finishes having written no answer at all. An empty string is not deliverable — it reaches a person as silence |

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
the CLI `index` verb (the standalone indexer takes the same values as CLI flags). The settings-file
baseline is **off**, but the shipped Service env template
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
| `Provider` | `Ollama` | `Rag__Provider` | `Ollama` or `LlamaCpp`. Set independently of `Llm:Provider` — the index is its own model |
| `Endpoint` | `http://localhost:11434` | `Rag__Endpoint` | Embedding server URL (may differ from chat) |
| `TimeoutSeconds` | `120` | `Rag__TimeoutSeconds` | Embed request timeout |
| `Sources` | `[]` | `Rag__Sources__0…` | Docs to index (files/dirs, recursive) |
| `SourcePattern` | `*.md` | `Rag__SourcePattern` | Glob when walking directories |
| `ChunkSize` | `2000` | `Rag__ChunkSize` | Chunk target (chars); changing forces a full rebuild |
| `ChunkOverlap` | `200` | `Rag__ChunkOverlap` | Chunk overlap (chars) |

### `Prompts` — the persona and tool definitions

**This is not an override layer; it is where the assistant's text lives.** The prompt segments and the
tool catalog are files, installed by `deploy/deploy.sh` from `deploy/prompts/` in this repo. Nothing
equivalent is compiled into the binary, so a service pointed at a directory that lacks them **refuses
to start**, naming the file it wants.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `Directory` | `<prefix>/prompts` | `Prompts__Directory` | `preamble.md`, `actions-allowed.md`, `actions-auto.md`, `actions-denied.md`, `voice.md`, `tools.json` |

The two kinds of file are read on different schedules, and the difference is deliberate:

- **The `.md` segments are re-read every turn.** Edit one and the next question uses it — no restart,
  no rebuild, no deploy. A blank or half-saved file counts as absent for that turn rather than
  blanking the prompt.
- **`tools.json` is read once, at startup.** It is the contract between the model and the dispatcher;
  swapping it under a turn already in flight would let a tool be offered and then not exist when it is
  called. Editing it takes a restart, and the restart is what validates it.

`tools.json` carries each tool's description, and each parameter's description, type, `required` flag
and `enum`. What it does **not** carry is which tier a tool belongs to — that decides who is offered it
and whether it is staged for confirmation, and it stays in code. A file that could move a staged
command into the read-only tier would be a privilege escalation.

Startup refuses a catalog that disagrees with the code: a tool the dispatcher can run and the file
omits (the model silently loses a capability), a tool the file invents and nothing implements (the
model calls it and the turn fails), a blank description, or an unknown parameter type.

⚠ **A deploy overwrites this directory** (`rsync --delete`). That is the intended loop — tune the file
on the running host, confirm the wording, then paste it back into `deploy/prompts/` so it ships. The
deploy is the commit. Anything not copied back is lost on the next one.

`voice.md` (inline: `Llm:Voice`) is the spoken-delivery segment, appended after the injected instance
and blueprint lists only for a turn whose caller asked for `style: "voice"`. It is last so it is the
final instruction the model reads before answering. Every other segment resolves the same way it
always does — file > inline `Llm:*` > the lib constant.

⚠ **A prompt directory is a prototyping tool.** Production runs the compiled-in text, and a file left
in this directory silently shadows it for every turn — including one that has drifted behind a
release. Point `Prompts__Directory` at an empty directory on a real host, and tune with
`kgsm-assistant-eval --shipped-prompts`, which measures the constants rather than local edits.

---

## Service-only sections

### `Urls` / `ASPNETCORE_URLS`

Bind address. Default `http://localhost:5180` (loopback). Override with the standard
`ASPNETCORE_URLS` env var (e.g. `http://127.0.0.1:5180`). TLS terminates at a reverse proxy.

### `Assistant` — action policy (`AssistantServiceOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ActionsEnabled` | `false` | `Assistant__ActionsEnabled` | Master switch for mutating actions |
| `Confirmation:TtlSeconds` | `300` | `Assistant__Confirmation__TtlSeconds` | Confirmation token lifetime |
| `Webhook:Secret` | _(empty)_ | `Assistant__Webhook__Secret` | **Secret.** HMAC for `POST /events`; empty ⇒ unverified (dev) |
| `Relay:Secret` | _(empty)_ | `Assistant__Relay__Secret` | **Secret.** Trusted-relay auth (e.g. kgsm-api); empty ⇒ relay path off |
| `Push:Enabled` | `true` | `Assistant__Push__Enabled` | Whether a waiting action may be announced by Web Push at all |
| `Push:Subject` | `https://github.com/TheKrystalShip/KGSM` | `Assistant__Push__Subject` | VAPID `sub` — a `mailto:`/`https:` contact URI for the sender |
| `Push:PresenceGraceSeconds` | `20` | `Assistant__Push__PresenceGraceSeconds` | How long after a surface closes somebody still counts as present |
| `Push:PollSeconds` | `5` | `Assistant__Push__PollSeconds` | How often waiting actions are re-examined |

**Web Push announces one thing:** an action the assistant staged and is waiting on, once the person it
is waiting on has no surface open. It is inert until a browser registers itself under the standalone
assistant's Settings → Notifications, and it never carries fleet events — a crash or a finished update
is the Control Panel's, on its own origin with its own key.

⚠ **The VAPID pair is generated once, into the state database, and must never be regenerated.** Its
public half is baked into every subscription a browser has already created, so a new pair silently
orphans every registered device with no error at either end. There is no config key for it precisely
so that no deploy can lose or replace it.

⚠ **`Push:PresenceGraceSeconds` spends a fixed budget.** A staged action lives `Confirmation:TtlSeconds`
(five minutes by default), so raising this trades fewer unnecessary notifications for less of the
approval window left to act in. It is deliberately shorter than the grace that keeps a turn running:
that one protects work in progress from a screen lock, this one is spending somebody's deadline.

### `KgsmAuth` — the host's Discord application (shared)

The **ecosystem's** block, identical across this service and the Control Panel API, so a sign-in goes
through the same application whichever door somebody knocks on. It lives once per host in
`/etc/kgsm/kgsm-auth.env`, which every leaf's unit loads before its own env file. Setting one of
these keys in *this* leaf's env overrides the shared value for this leaf alone.

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `ClientId` | _(empty)_ | `KgsmAuth__Providers__discord__ClientId` | OAuth app id |
| `ClientSecret` | _(empty)_ | `KgsmAuth__Providers__discord__ClientSecret` | **Secret.** code→token exchange |

The same file carries `GuildId`, `BotToken` and the role id lists. Those are **kgsm-bot's** — a
person typing a slash command has proved nothing but their Discord account, so the bot maps a guild
role to a tier. Nothing here reads them: a sign-in establishes who someone is, and what they may do
is on their KGSM account (`Auth:UsersDbPath`).

### `DiscordOAuth` — this surface's own sign-in (`DiscordOAuthOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `RedirectUri` | _(empty)_ | `DiscordOAuth__RedirectUri` | This service's `/auth/discord/callback`; must match the Developer Portal exactly |
| `Scopes` | `identify` | `DiscordOAuth__Scopes` | Enough to establish who someone is, which is all a sign-in decides |

### `Auth` — sessions & CORS (`AuthOptions`)

| Key | Default | Env | Notes |
|-----|---------|-----|-------|
| `SigningKey` | _(empty)_ | `Auth__SigningKey` | **Secret.** Signs session tokens; empty ⇒ this host generates one on first start and keeps it in `<state-dir>/signing-key` (0600) |
| `HostId` | _(empty)_ | `Auth__HostId` | Token audience; empty ⇒ the machine name. A bearer minted here is refused by any other host |
| `AccessTtlSeconds` | `900` | `Auth__AccessTtlSeconds` | Access-bearer lifetime — short, because it is what bounds privilege between re-checks |
| `SessionTtlSeconds` | `2592000` | `Auth__SessionTtlSeconds` | Absolute sign-in cap (30d). Each refresh slides it forward |
| `RoleCacheTtlSeconds` | `60` | `Auth__RoleCacheTtlSeconds` | Per-user authority cache; also the staleness bound on a revoked role |
| `StateTtlSeconds` | `300` | `Auth__StateTtlSeconds` | How long an in-flight sign-in's handshake cookie lives |
| `AllowedOrigins` | `[]` | `Auth__AllowedOrigins__0…` | Origins (scheme+host, no trailing slash) a browser client may call from **and** be returned to after a sign-in; empty ⇒ SPA blocked |

`AllowedOrigins` is one list doing both jobs on purpose: a client trusted to call this service with a
bearer is exactly a client trusted to be handed one, and two lists would drift apart. It gates CORS,
and it gates the `return_to` on `/auth/discord/start` — a browser client asking to be sent back to
itself with the session instead of receiving the JSON a programmatic caller gets. An unlisted
`return_to` is refused at `/start`, before the bounce to Discord, and the address is checked again at
the callback: the cookie carrying it between the two is client-held and carries no integrity of its
own, so one hand-set cookie would otherwise be an open redirect that hands over a real session.

On a completed browser sign-in the callback `302`s to that address with the result in the URL
**fragment** — `#access=…&refresh=…&tier=…`, or `#error=<code>` — never the query, because a
fragment is not sent to a server, kept in a `Referer`, or written to an access log. The key names
match the ones kgsm-api hands back, so one client reads either.

Sessions are rows in the same SQLite file as the conversation history (`Conversation:DatabasePath`),
so they survive a restart and a revocation outlives the process that performed it.

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
