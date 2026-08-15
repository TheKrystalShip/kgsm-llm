# Serving the models with llama.cpp

The assistant reaches its chat model through `Llm:Provider` and its embedding model through
`Rag:Provider`. Each takes `Ollama` or `LlamaCpp`, they are set independently, and nothing above
`ILlmClient` / `IEmbeddingClient` knows which answered.

This directory holds what the `LlamaCpp` side needs: two units, one per model, and the env file
they read. **Nothing here is installed by `deploy.sh`** — these are host units that need root and
that decide what occupies the GPU, so they are put in place deliberately.

## What it buys, and what it costs

llama.cpp exposes knobs Ollama chooses for you: KV-cache precision, concurrent slots, batch sizes,
and multi-token prediction. It also takes them away as a managed service — a model is loaded by the
unit that starts it, not on demand by name.

The one thing to know before switching: **Ollama does not use llama.cpp's tool-calling path.** It
runs `llama-server` with `--no-jinja --chat-template chatml` and parses tool calls itself, above the
server. Going direct means `--jinja` and the model's own template, which is a different tool-call
encoding. Everything else here is mechanical; that is not. Re-run the eval and compare against the
recorded baseline before trusting it:

```bash
dotnet run --project ../../TheKrystalShip.Kgsm.Assistant.Eval -- --model gemma4:12b --shipped-prompts
```

## Setup

```bash
sudo ./install.sh     # units + env template, templated to the assistant's user and the llama-server found
```

Then put the GGUFs where `/etc/kgsm-assistant/llama-server.env` points.

⚠ **Take the models from their published GGUF repo, not from Ollama's blob store.** Ollama's blobs
are not portable: its embeddinggemma blob fails to load in mainline llama.cpp with *wrong number of
tensors; expected 316, got 314*, even though Ollama's own bundled server reads it happily.
`ggml-org` and `unsloth` publish ones that work.

Switching is one command, because the units and the assistant's configuration have to move
together — pointing one at llama.cpp while the other still expects Ollama is how the assistant ends
up talking to a port nothing is listening on:

```bash
sudo ./use-backend.sh llamacpp     # or: ollama
./use-backend.sh status
```

Confirm the chat server loaded a template that can call tools — an empty
`chat_template_tool_use` is the failure that looks like a model being unhelpful rather than a
misconfiguration:

```bash
curl -s localhost:8081/props | jq '{chat_template, chat_template_tool_use}'
```

`use-backend.sh` writes those keys into `/etc/kgsm-assistant/service.env` itself, including
`Llm__ContextWindow` — the server fixes the window at launch and the assistant measures token usage
against the configured number, so the two disagreeing makes every usage figure wrong with nothing
reporting an error.

⚠ **Rebuild the RAG index after switching the embedding backend.** The indexer is incremental by
content hash, and the index header records the embedding model's *name* — which does not change when
the server behind it does. A switch therefore reports `0 embedded, N reused` and quietly keeps the
previous embedder's vectors. Force it:

```bash
sudo systemctl stop kgsm-rag-indexer
sudo rm /var/lib/kgsm-assistant/rag-index.krag
sudo systemctl start kgsm-rag-indexer      # expect "N embedded, 0 reused"
```

## VRAM

Both models stay resident for the life of their unit — there is no idle eviction, which is the
point on a host that wants no cold-load latency and the constraint on a host that is tight. Budget
for both sets of weights plus each server's KV cache at the context it was launched with, alongside
anything else on the card (`kgsm-speech` holds whisper and kokoro while it is running).

`kgsm-llama-chat` declares `Conflicts=ollama.service`: one GPU holds one copy of the weights, and
two services each reserving them is how the second one fails to allocate. Drop that line on a host
with the headroom to run both.
