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
sudo install -d -o kgsm -g kgsm /var/lib/llama/models
# put the GGUFs there, then:
sudo cp llama-server.env.example /etc/kgsm-assistant/llama-server.env
sudo $EDITOR /etc/kgsm-assistant/llama-server.env      # paths, ports, context sizes
sudo cp kgsm-llama-chat.service kgsm-llama-embed.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now kgsm-llama-chat kgsm-llama-embed
```

Confirm the chat server loaded a template that can call tools — an empty
`chat_template_tool_use` is the failure that looks like a model being unhelpful rather than a
misconfiguration:

```bash
curl -s localhost:8081/props | jq '{chat_template, chat_template_tool_use}'
```

Then point the assistant at them, in `/etc/kgsm-assistant/service.env`:

```ini
Llm__Provider=LlamaCpp
Llm__Endpoint=http://127.0.0.1:8081
Llm__ContextWindow=32768        # must equal LLAMA_CHAT_CTX
Rag__Provider=LlamaCpp
Rag__Endpoint=http://127.0.0.1:8082
```

and restart: `systemctl restart kgsm-assistant-service`.

## VRAM

Both models stay resident for the life of their unit — there is no idle eviction, which is the
point on a host that wants no cold-load latency and the constraint on a host that is tight. Budget
for both sets of weights plus each server's KV cache at the context it was launched with, alongside
anything else on the card (`kgsm-speech` holds whisper and kokoro while it is running).

`kgsm-llama-chat` declares `Conflicts=ollama.service`: one GPU holds one copy of the weights, and
two services each reserving them is how the second one fails to allocate. Drop that line on a host
with the headroom to run both.
