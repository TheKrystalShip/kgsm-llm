# deploy/ — systemd units & env template

Deployment tooling for the two long-running KGSM Assistant artifacts. This is the
*quick* install reference; the full cold-start runbook (prerequisites, build, Ollama
tuning, RAG end-to-end, reverse proxy, troubleshooting) is **[../docs/DEPLOYMENT.md](../docs/DEPLOYMENT.md)**.

| File | What it deploys |
|------|-----------------|
| `kgsm-assistant-service.service` | The HTTP/SSE turn API (framework-dependent — needs the .NET 10 runtime on the host) |
| `kgsm-rag-indexer.service` | The RAG indexer in `--watch` mode (Native-AOT — standalone binary, no runtime) |
| `assistant.env.example` | Secrets + non-default config for the Service (copy to `/etc/kgsm-assistant/service.env`) |

The **CLI** (`kgsm-assistant`) is an interactive tool, not a daemon — it has no unit here.

## Install (after building — see the runbook)

```bash
# 1. Lay down the artifacts (paths match the unit files; adjust both if you change them).
sudo install -d /opt/kgsm-assistant/service /opt/kgsm-assistant/indexer \
                /opt/kgsm-assistant/docs   /var/lib/kgsm-assistant
sudo cp -r <service-publish>/*  /opt/kgsm-assistant/service/      # framework-dependent publish
sudo install <indexer-publish>/kgsm-rag-indexer /opt/kgsm-assistant/indexer/   # AOT binary

# 2. Secrets + config.
sudo install -d /etc/kgsm-assistant
sudo cp deploy/assistant.env.example /etc/kgsm-assistant/service.env
sudo chmod 600 /etc/kgsm-assistant/service.env
sudo "$EDITOR" /etc/kgsm-assistant/service.env                   # fill in the real values

# 3. Units.
sudo cp deploy/kgsm-assistant-service.service deploy/kgsm-rag-indexer.service \
        /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now kgsm-assistant-service          # the API
sudo systemctl enable --now kgsm-rag-indexer               # only if using RAG

# 4. Verify.
curl -fsS http://127.0.0.1:5180/health        # -> {"status":"ok"}
journalctl -u kgsm-assistant-service -n 30 --no-pager
```

## Before you copy the units, read these

- **`User=kgsm`** is a placeholder. Run the Service as the user that **owns the kgsm
  instance registry** (`~/.local/share/kgsm`); otherwise the assistant sees zero servers.
- **The indexer is ordered `After=ollama.service`** on purpose: it embeds via Ollama at
  startup and does *not* retry a failed initial build. If your Ollama isn't a systemd unit
  named `ollama.service`, fix the ordering or the index can start stale.
- **`Rag:IndexPath` in the Service env must equal `--index` in the indexer unit** — that
  one file is the entire producer→consumer contract. The Service hot-reloads it on change.
- These units assume **framework-dependent** Service + **AOT** indexer. If you publish the
  Service self-contained instead, change its `ExecStart` to the native launcher.
