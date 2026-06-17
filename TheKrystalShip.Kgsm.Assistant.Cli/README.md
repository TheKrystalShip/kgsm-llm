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

`/exit` (or `/quit`) leave · `/reset` start a fresh conversation · `/help` show help ·
**Ctrl-C** cancels the current reply (stays in the REPL) · **Ctrl-D** leaves.

## Authority

The person at the terminal already has shell + direct `kgsm.sh` access, so the CLI is
**authorized by default** — it can propose and (after you confirm) run mutating actions.
`--read-only` demotes a session to reads. A staged destructive op (e.g. uninstall) is always
gated by an interactive `y/N` prompt; **if stdin is not a terminal (piped/scripted), the
proposal is printed but never executed.**

Actions are attributed to `cli:<your-os-user>` in the kgsm audit trail.

## Configuration

Lowest → highest precedence:

1. **Built-in defaults** (Ollama `http://localhost:11434` / `gemma4:12b`; `KGSM:Path`
   defaults to `/opt/kgsm/kgsm.sh`).
2. **A config file**, if present: `$KGSM_ASSISTANT_CONFIG`, else
   `~/.config/kgsm-assistant/appsettings.json` (or `$XDG_CONFIG_HOME/kgsm-assistant/...`).
3. **Environment variables** (`Section__Key`) — the path for secrets.
4. **`--model` / `--config`** flags.

Common settings:

| Key | Env var | Notes |
|---|---|---|
| `KGSM:Path` | `KGSM__Path` | Path to `kgsm.sh` (required; defaults to `/opt/kgsm/kgsm.sh`). |
| `Ollama:Endpoint` | `Ollama__Endpoint` | Ollama base URL. |
| `Ollama:Model` | `Ollama__Model` | Model tag (also `--model`). |
| `WebSearch:ApiKey` | `WebSearch__ApiKey` | Tavily key. **ENV-only**; empty ⇒ web search disabled (fails closed). |

Example `~/.config/kgsm-assistant/appsettings.json`:

```json
{
  "KGSM":   { "Path": "/opt/kgsm/kgsm.sh" },
  "Ollama": { "Model": "gemma4:12b" }
}
```

The Tavily key should **only** ever come from the environment (`WebSearch__ApiKey=tvly-…`),
never a committed file.

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

Only the single `kgsm-assistant` file needs deploying (any `.pdb`/`.xml` beside it are ignorable).

## Exit codes

`0` ok · `1` runtime failure (turn error, or a confirmed action failed) · `2` usage/config
error · `130` cancelled (Ctrl-C).
