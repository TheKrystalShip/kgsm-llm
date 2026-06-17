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

Tool progress (`⚙ get_status(...)`), the REPL prompt, confirmations, and logs all go to
**stderr**, and only when stdout is a TTY — a redirected/piped reply stays plain text.

### Options

| Flag | Effect |
|---|---|
| `--read-only` | Reads only — never offer or run mutating/destructive actions. |
| `--model <tag>` | Override the Ollama model (e.g. `gemma4:12b`). |
| `--config <path>` | Use this config file instead of the default location. |
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
| `InventoryCache:InstancesTtlSeconds` | `InventoryCache__InstancesTtlSeconds` | `300` | Instance-list cache TTL. |
| `InventoryCache:BlueprintsTtlSeconds` | `InventoryCache__BlueprintsTtlSeconds` | `600` | Blueprint-list cache TTL. |
| `WebSearch:ApiKey` | `WebSearch__ApiKey` | *(empty)* | Tavily key. **ENV-only**; empty ⇒ web search disabled (fails closed). |
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
export WebSearch__ApiKey=tvly-…       # web search enabled while this is set; absent ⇒ disabled
```

For a long-lived setup, put it in a systemd `EnvironmentFile=`, your shell profile, or an env
file your launcher sources — never in `appsettings.json`.

## Build & install

Shipped as a self-contained single-file binary (bundles the .NET runtime — runs on a host
with no SDK installed). No trimming (the Ollama client uses `System.Text.Json` reflection).

```bash
dotnet publish TheKrystalShip.Kgsm.Assistant.Cli \
  -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

sudo install -m 0755 \
  TheKrystalShip.Kgsm.Assistant.Cli/bin/Release/net9.0/linux-x64/publish/kgsm-assistant \
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
  TheKrystalShip.Kgsm.Assistant.Cli/bin/Release/net9.0/linux-x64/publish/appsettings.json \
  /usr/local/bin/appsettings.json
```

## Exit codes

`0` ok · `1` runtime failure (turn error, or a confirmed action failed) · `2` usage/config
error · `130` cancelled (Ctrl-C).
