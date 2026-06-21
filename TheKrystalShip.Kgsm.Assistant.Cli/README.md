# kgsm-assistant (CLI)

A terminal surface onto the KGSM server assistant — ask about / act on your game servers
straight from the shell, no Discord and no browser. It is a thin console **host** on the
same backend as the Discord bot and the HTTP/SSE service: the whole backend is three calls
(`AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters`), with no HTTP/OAuth/SSE.

It is a standalone **leaf** in the KGSM ecosystem: it runs co-located with a `kgsm`, needs
only a local [Ollama](https://ollama.com) + kgsm-lib (and, optionally, a Tavily key for web
search), and never depends on a sibling leaf or the web API.

## Usage

```bash
kgsm-assistant "is terraria up?"          # one-shot: prints the reply, exits
echo "what's installed?" | kgsm-assistant # one-shot from piped stdin
kgsm-assistant                            # interactive REPL (a TTY with no prompt)
```

stdout carries **only** the assistant's reply, so it pipes cleanly:

```bash
kgsm-assistant "list the servers" | grep minecraft
```

Tool progress (`⚙ get_status(...)`), the REPL prompt, confirmations, the context line, and
logs all go to **stderr**, and only when stdout is a TTY — a redirected/piped reply stays
plain text.

### Rich terminal output

On an interactive terminal (and never when piped), the reply is rendered with a small, safe
subset of Markdown — **bold**, `inline code`, `#` headers, `-`/`*`/`+` bullets and fenced
` ``` ` code blocks. Italic is deliberately *not* rendered: a single `*`/`_` would mangle
identifiers like `instance_name`. A braille spinner (`⠙ thinking…` / `⠹ working…`) fills the
gaps where there is nothing to show yet — the model's first-token latency and the round-trip
after each tool — and erases itself the instant output starts. The REPL prompt is a colored
`❯`, tool lines colorize the `⚙`/`✓`, and all of it is gated exactly like the status lines:

* it's all on a **TTY only** — a redirected/piped reply is the model's **raw Markdown text**,
  byte-for-byte (so `kgsm-assistant "…" | cat` still pipes cleanly), and
* `--no-color` / `NO_COLOR` drop the color (the spinner stays, just uncolored).

### Context usage

After each reply the assistant prints how much of the model's context window the turn used, in
**tokens** (never a percentage):

```
context: 2,102 / 32,768 tokens (30,666 free)
```

That is `prompt + reply` tokens (what the model actually processed) over the configured
`Ollama:NumCtx` window. Because the conversation is a rolling window with a fresh system prompt
each turn, this is per-turn occupancy — which is also why **`/compact`** visibly drops it. It's
shown on stderr on a TTY only, so piped output stays clean. (Every surface exposes the same
figure: the HTTP service puts it on the SSE `done` event and the buffered `/turn` response.)

### Conversation recording (for self-improvement)

To analyse how the assistant behaves and improve the toolkit and system prompts over time, each
completed turn is appended — **on by default in the CLI** — to a daily transcript file:

```
~/.local/share/kgsm-assistant/transcripts/2026-06-17.jsonl   # $XDG_DATA_HOME honored
```

One JSON object per turn (one `.jsonl` line), capturing what you need to study and tune behaviour:
the user prompt, the **full tool trajectory** (each tool the model picked, its arguments, the *raw*
result it got back, and the call latency), the iteration count and whether the safety cap was hit,
the final reply, token usage, the model/temperature, the **system-prompt hash** (so you can bucket
turns *before vs after* a prompt change), and the outcome (`ok` / `error` / `cap-hit` / `cancelled` —
yes, turns you abandon with Ctrl-C are recorded too, so you can see where chats go wrong). Grep it,
`jq` it, or feed whole days to a model:

```bash
jq -r 'select(.outcome=="cap-hit") | .user' ~/.local/share/kgsm-assistant/transcripts/*.jsonl
jq '.tools[].name' ~/.local/share/kgsm-assistant/transcripts/*.jsonl | sort | uniq -c | sort -rn
```

This is a **separate concern from the model's working memory** (the rolling window `/compact` trims):
the corpus is append-only and never trimmed, reset, or overwritten — so it survives compaction. It's
also a **shared-core mechanism**: the recorder lives in the agent loop every surface composes through,
so the HTTP service and Discord bot can opt in later with the same switch (off by default there).

It records *your own* chats on *your own* host; no secrets transit tool arguments (the Tavily key is
env/config, never an argument). To turn it off, set `Recording:Enabled` to `false` (or
`Recording__Enabled=false`); to relocate the corpus, set `Recording:Directory`.

### Tuning prompts & tool descriptions (no recompile)

The text that steers the model — the system prompt and the tool/parameter descriptions — lives in
editable files so you can tune it against the recorded corpus without rebuilding. Seed the defaults:

```bash
kgsm-assistant --dump-prompts        # writes editable copies, never clobbering your edits
```

That creates, under `~/.config/kgsm-assistant/prompts/` (`$XDG_CONFIG_HOME` honored):

| File | What it steers |
|---|---|
| `preamble.md` | The assistant's persona + how it uses the live lists. |
| `actions-allowed.md` | Appended for authorized callers (the propose-only command stance). |
| `actions-denied.md` | Appended for read-only callers. |
| `tools.json` | Per-tool and per-parameter **descriptions** (the routing levers). Tool *names* are structural and not overridable here. |

Edit a file and the change applies on the **next turn** — no restart (the files are re-read each
turn). Precedence is **file > `Llm:*` config > the built-in default**, and a blank/absent file falls
back to the default (so a mid-save read never blanks the prompt). `tools.json` overrides only the
prose you include; anything omitted keeps its default.

The loop this closes: edit → chat → read the transcripts → see whether the change actually moved the
model's behavior → iterate. Two things make experiments comparable in the corpus:

* **`--label <name>`** tags every turn of a run (e.g. `kgsm-assistant --label preamble-v2 "…"`), the
  robust bucketing key — `jq 'select(.label=="preamble-v2")' …`.
* **`sysHash`** auto-fingerprints the editable prompt template (excluding the live lists, so it moves
  when you tune the persona, not when a server is installed mid-session).

### Options

| Flag | Effect |
|---|---|
| `--read-only` | Reads only — never offer or run mutating/destructive actions. |
| `--model <tag>` | Override the Ollama model (e.g. `gemma4:12b`). |
| `--config <path>` | Use this config file instead of the default location. |
| `--label <name>` | Tag this run's recorded turns, to A/B a prompt/tool-description edit. |
| `--dump-prompts` | Write editable default prompt + `tools.json` files (then exit); never clobbers edits. |
| `--no-color` | Disable color (also honored: the `NO_COLOR` env var). |
| `--verbose` | Show debug logs on stderr (default is quiet — warnings only). |
| `-h`, `--help` | Show usage and exit. |

### REPL commands

`/exit` (or `/quit`) leave · `/reset` start a fresh conversation · `/compact` summarize
this conversation in place · `/help` show help · **Ctrl-C** cancels the current reply (stays
in the REPL) · **Ctrl-D** leaves.

**`/compact`** is the conversation analogue of context compaction: it asks the model to
summarize the conversation so far, then **replaces** the in-session history with that single
summary — freeing context while keeping continuity (the assistant still remembers what you
established). Unlike `/reset` (which throws the history away), `/compact` keeps the gist. It's
a no-op on a near-empty conversation, leaves the history untouched if the summary fails, and is
cancellable with Ctrl-C. The summary lives only in memory for the session.

## Authority

The person at the terminal already has shell + direct `kgsm.sh` access, so the CLI is
**authorized by default** — it can propose and (after you confirm) run mutating actions.
`--read-only` demotes a session to reads. A staged destructive op (e.g. uninstall) is always
gated by an interactive `y/N` prompt; **if stdin is not a terminal (piped/scripted), the
proposal is printed but never executed.**

Actions are attributed to `cli:<your-os-user>` in the kgsm audit trail.

## Configuration

Every knob lives in **one** place: `appsettings.json`. There are no defaults baked into the
code. That file is shipped two ways — **embedded** in the binary (so the lone executable carries
its full defaults and stands on its own legs with zero extra files) and **copied next to the
binary** as a readable, editable template. You configure the CLI by editing a JSON file or
setting environment variables; nothing requires recompiling.

Config layers, lowest → highest precedence (each overrides the one before it):

1. **Embedded defaults** — the `appsettings.json` baked into the binary at build time.
2. **The sidecar `appsettings.json`** shipped next to the binary (edit this to change a default
   host-wide).
3. **Your config file**, if present: `$KGSM_ASSISTANT_CONFIG`, else
   `~/.config/kgsm-assistant/appsettings.json` (or `$XDG_CONFIG_HOME/kgsm-assistant/...`), or
   whatever `--config <path>` points at. Per-user overrides without touching the system file.
4. **Environment variables** (`Section__Key`, double-underscore) — the channel for secrets.
5. **`--model`** flag — wins over everything for the model tag.

### The full configurable surface

| Key | Env var | Default | Notes |
|---|---|---|---|
| `KGSM:Path` | `KGSM__Path` | `/opt/kgsm/kgsm.sh` | Path to this host's `kgsm.sh` (required; validated at startup). |
| `Ollama:Endpoint` | `Ollama__Endpoint` | `http://localhost:11434` | Ollama base URL. |
| `Ollama:Model` | `Ollama__Model` | `gemma4:12b` | Model tag (also `--model`). |
| `Ollama:NumCtx` | `Ollama__NumCtx` | `32768` | KV-cache context window (fixed VRAM reservation). |
| `Ollama:TimeoutSeconds` | `Ollama__TimeoutSeconds` | `300` | Per-request generation timeout. |
| `Ollama:Temperature` | `Ollama__Temperature` | `0.3` | Sampling temperature (low keeps tool routing reliable). |
| `Conversation:MaxMessages` | `Conversation__MaxMessages` | `12` | REPL short-term memory depth. |
| `Conversation:IdleTimeoutMinutes` | `Conversation__IdleTimeoutMinutes` | `15` | Idle reset window. |
| `LlmAgent:MaxIterations` | `LlmAgent__MaxIterations` | `8` | Safety cap on model↔tool round-trips per turn. |
| `LlmAgent:MaxToolOutputChars` | `LlmAgent__MaxToolOutputChars` | `1500` | Tool-output truncation fed back to the model. |
| `Recording:Enabled` | `Recording__Enabled` | `true` (CLI) | Append each turn to the transcript corpus (see [Conversation recording](#conversation-recording-for-self-improvement)). |
| `Recording:Directory` | `Recording__Directory` | *(XDG data home)* | Where daily `yyyy-MM-dd.jsonl` transcripts are written. Empty ⇒ `~/.local/share/kgsm-assistant/transcripts`. |
| `Recording:Label` | `Recording__Label` | *(empty)* | Experiment label stamped on each recorded turn (also `--label`). |
| `Prompts:Directory` | `Prompts__Directory` | *(XDG config home)* | Editable prompt/tool-description files (see [Tuning](#tuning-prompts--tool-descriptions-no-recompile)). Empty ⇒ `~/.config/kgsm-assistant/prompts`. |
| `InventoryCache:InstancesTtlSeconds` | `InventoryCache__InstancesTtlSeconds` | `300` | Instance-list cache TTL. |
| `InventoryCache:BlueprintsTtlSeconds` | `InventoryCache__BlueprintsTtlSeconds` | `600` | Blueprint-list cache TTL. |
| `WebSearch:ApiKey` | `WebSearch__ApiKey` | *(empty)* | Tavily key — backs the web-fallback half of the `search` tool. **ENV-only**; empty ⇒ no web fallback (and, with RAG also off, the `search` tool is omitted entirely). |
| `WebSearch:MaxResults` | `WebSearch__MaxResults` | `4` | Results per search. |
| `WebSearch:SearchDepth` | `WebSearch__SearchDepth` | `basic` | `basic` (1 credit) or `advanced` (2). |
| `WebSearch:TimeoutSeconds` | `WebSearch__TimeoutSeconds` | `10` | Per-search timeout (the agent loop blocks on it). |
| `WebSearch:MaxCallsPerDay` | `WebSearch__MaxCallsPerDay` | `200` | Process-wide daily spend backstop. |

Example per-user override `~/.config/kgsm-assistant/appsettings.json` — only the keys you want
to change; everything else falls through to the layers below:

```json
{
  "KGSM":   { "Path": "/srv/kgsm/kgsm.sh" },
  "Ollama": { "Model": "gemma4:12b" }
}
```

### Secrets (the Tavily key)

The Tavily API key is the one value that must **never** sit in a committed/shipped file. Supply
it only through the environment, e.g.

```bash
export WebSearch__ApiKey=tvly-…       # enables the `search` tool's web fallback while set; absent ⇒ local-only (or no `search` if RAG is also off)
```

For a long-lived setup, put it in a systemd `EnvironmentFile=`, your shell profile, or an env
file your launcher sources — never in `appsettings.json`.

## Build & install

Two distribution options, both fine (the CLI has no daemon/`ExecStart` to keep consistent):

- **Self-contained single-file** (below) — bundles the .NET runtime; runs on a host with no SDK
  or runtime installed. Larger, fully portable. No trimming (the Ollama client uses
  `System.Text.Json` reflection).
- **Framework-dependent** (`dotnet publish -c Release -o out/cli`, ~4 MB) — needs the .NET 10
  runtime present; this is what the [deployment runbook](../docs/DEPLOYMENT.md#5--the-cli-simplest-path--start-here)
  uses since the host already has it.

```bash
dotnet publish TheKrystalShip.Kgsm.Assistant.Cli \
  -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

sudo install -m 0755 \
  TheKrystalShip.Kgsm.Assistant.Cli/bin/Release/net10.0/linux-x64/publish/kgsm-assistant \
  /usr/local/bin/kgsm-assistant
```

The publish dir contains two files worth deploying: the `kgsm-assistant` binary and an
`appsettings.json` beside it (any `.pdb`/`.xml` are ignorable). The defaults are **embedded**
in the binary, so `kgsm-assistant` runs standalone even if you deploy only the binary — but
shipping the sidecar `appsettings.json` gives operators a documented file to tune host-wide
(install it next to the binary, or anywhere and point `$KGSM_ASSISTANT_CONFIG` / `--config` at
it). Edit it, or layer a per-user `~/.config/kgsm-assistant/appsettings.json` on top — see
[Configuration](#configuration).

```bash
# optional: ship the editable template alongside the binary
sudo install -m 0644 \
  TheKrystalShip.Kgsm.Assistant.Cli/bin/Release/net10.0/linux-x64/publish/appsettings.json \
  /usr/local/bin/appsettings.json
```

## Exit codes

`0` ok · `1` runtime failure (turn error, or a confirmed action failed) · `2` usage/config
error · `130` cancelled (Ctrl-C).
