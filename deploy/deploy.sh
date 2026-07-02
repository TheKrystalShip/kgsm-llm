#!/usr/bin/env bash
#
# Build + deploy the kgsm-llm assistant in one go.
#
#   ./deploy/deploy.sh                 # Service (HTTP/SSE) + CLI (on PATH)
#   ./deploy/deploy.sh --with-indexer  # also build + enable the RAG indexer (needs Ollama)
#
# Publishes as YOU (the invoking, service-owning user) and uses sudo ONLY for the steps that
# touch systemd / root-owned paths. Three artifacts (see docs/DEPLOYMENT.md):
#   * Service  — framework-dependent single file → /opt/kgsm-assistant/service  (systemd, :5180)
#   * CLI      — framework-dependent single file → /opt/kgsm-assistant/cli + symlinked on PATH
#   * Indexer  — Native-AOT single file          → /opt/kgsm-assistant/indexer (opt-in; needs Ollama)
#
# Service + CLI need the .NET 10 ASP.NET Core runtime on the host (checked up front). The Service
# and CLI ProjectReference the sibling kgsm-lib, so the full tks workspace (umbrella checkout) must
# be present. The env file /etc/kgsm-assistant/service.env is created from the template only if
# absent and NEVER overwritten — your secrets survive a redeploy.
#
# Non-interactive: SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh
#
set -euo pipefail

WITH_INDEXER=0
[[ "${1:-}" == "--with-indexer" ]] && WITH_INDEXER=1

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-assistant"
STATE_DIR="/var/lib/kgsm-assistant"
ENV_DIR="/etc/kgsm-assistant"
ENV_FILE="$ENV_DIR/service.env"
SVC_UNIT_DST="/etc/systemd/system/kgsm-assistant-service.service"
IDX_UNIT_DST="/etc/systemd/system/kgsm-rag-indexer.service"
CLI_LINK="/usr/local/bin/kgsm-assistant-cli"
SERVICE="kgsm-assistant-service"
INDEXER="kgsm-rag-indexer"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE="$(cd "$REPO_DIR/.." && pwd)"
SVC_PROJ="$REPO_DIR/TheKrystalShip.Kgsm.Assistant.Service/TheKrystalShip.Kgsm.Assistant.Service.csproj"
CLI_PROJ="$REPO_DIR/TheKrystalShip.Kgsm.Assistant.Cli/TheKrystalShip.Kgsm.Assistant.Cli.csproj"
IDX_PROJ="$REPO_DIR/TheKrystalShip.Rag.Indexer/TheKrystalShip.Rag.Indexer.csproj"
SVC_UNIT_SRC="$REPO_DIR/deploy/kgsm-assistant-service.service"
IDX_UNIT_SRC="$REPO_DIR/deploy/kgsm-rag-indexer.service"
ENV_EXAMPLE="$REPO_DIR/deploy/assistant.env.example"
PUB="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
SVC_USER="${KGSM_ASSISTANT_USER:-$(id -un)}"
SVC_GROUP="${KGSM_ASSISTANT_GROUP:-$(id -gn)}"
SUDO="${SUDO:-sudo}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5180/health}"
HEALTH_TRIES="${HEALTH_TRIES:-30}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap — attempting to bring it back up..."
        $SUDO systemctl start "$SERVICE" \
            && err "restarted ${SERVICE} (running the PREVIOUS build)." \
            || err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        curl -fsS -o /dev/null --max-time 2 "$HEALTH_URL" 2>/dev/null && return 0
        sleep 1
    done
    return 1
}

# Render a unit with the service user substituted in, then install it only if it changed.
# Sets UNIT_CHANGED=1 when it touched the destination.
UNIT_CHANGED=0
install_unit() {  # src  dst
    local src="$1" dst="$2" tmp
    tmp="$(mktemp)"
    sed "s/^User=.*/User=${SVC_USER}/; s/^Group=.*/Group=${SVC_GROUP}/" "$src" > "$tmp"
    if ! cmp -s "$tmp" "$dst"; then
        log "installing systemd unit → ${dst} (User=${SVC_USER})"
        $SUDO install -m 0644 "$tmp" "$dst"
        UNIT_CHANGED=1
    fi
    rm -f "$tmp"
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the service user. It builds as you and sudo's only the systemd steps."
    exit 1
fi
[[ -f "$SVC_PROJ" ]] || { err "project not found: $SVC_PROJ"; exit 1; }
[[ -d "$WORKSPACE/kgsm-lib" ]] || { err "sibling repo missing: $WORKSPACE/kgsm-lib — clone the full tks workspace (umbrella checkout)."; exit 1; }
if ! dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 10\.'; then
    err "the .NET 10 ASP.NET Core shared runtime is not installed (need 'Microsoft.AspNetCore.App 10.x'). Check: dotnet --list-runtimes"
    exit 1
fi

# ── 1. Build (as the invoking user — fail fast before any disruption) ──────────
log "publishing Service (framework-dependent single-file, ${RID})"
rm -rf "$PUB"
dotnet publish "$SVC_PROJ" -c Release -r "$RID" --no-self-contained -o "$PUB/service"
log "publishing CLI (framework-dependent single-file, ${RID})"
dotnet publish "$CLI_PROJ" -c Release -r "$RID" --no-self-contained -o "$PUB/cli"
if [[ "$WITH_INDEXER" -eq 1 ]]; then
    log "publishing RAG indexer (Native-AOT, ${RID}) — this is slow (ILC compile)..."
    dotnet publish "$IDX_PROJ" -c Release -r "$RID" -o "$PUB/indexer"
fi

# ── 2. Privileged prep (no service disruption yet) ────────────────────────────
log "ensuring install + state dirs (owned by ${SVC_USER})"
# Create owned by the service user, or take ownership if a prior root-era install (the old root
# install flow) left a dir root-owned — otherwise the non-sudo rsync below would fail mid-swap.
ensure_owned() {  # dir
    local d="$1"
    if [[ ! -d "$d" ]]; then
        $SUDO install -d -m 0755 -o "$SVC_USER" -g "$SVC_GROUP" "$d"
    elif [[ ! -w "$d" ]]; then
        log "taking ownership of $d (was not writable by $(id -un))"
        $SUDO chown -R "$SVC_USER:$SVC_GROUP" "$d"
    fi
}
for d in "$PREFIX" "$PREFIX/service" "$PREFIX/cli" "$PREFIX/docs" "$STATE_DIR"; do
    ensure_owned "$d"
done
[[ "$WITH_INDEXER" -eq 1 ]] && ensure_owned "$PREFIX/indexer"

# Env file: REQUIRED by the Service unit. Create from template only if absent; never clobber secrets.
ENV_FRESH=0
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template — EDIT IT (Discord creds, Assistant__Confirmation__Key, etc.)"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0640 -g "$SVC_GROUP" "$ENV_EXAMPLE" "$ENV_FILE"
    ENV_FRESH=1
fi

# Units (user-substituted, install-if-changed).
install_unit "$SVC_UNIT_SRC" "$SVC_UNIT_DST"
[[ "$WITH_INDEXER" -eq 1 ]] && install_unit "$IDX_UNIT_SRC" "$IDX_UNIT_DST"

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
$SUDO systemctl stop "$SERVICE" 2>/dev/null || true
STOPPED=1
[[ "$WITH_INDEXER" -eq 1 ]] && $SUDO systemctl stop "$INDEXER" 2>/dev/null || true

log "syncing publish trees → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUB/service/" "$PREFIX/service/"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUB/cli/"     "$PREFIX/cli/"
[[ "$WITH_INDEXER" -eq 1 ]] && rsync -a --delete "$PUB/indexer/" "$PREFIX/indexer/"

log "symlinking CLI → ${CLI_LINK}"
$SUDO ln -sfn "$PREFIX/cli/kgsm-assistant-cli" "$CLI_LINK"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

log "enabling + starting ${SERVICE}"
$SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
STOPPED=0

# Indexer: only auto-enable if Ollama is present (its unit Requires=ollama.service → enabling
# without Ollama would fail). Otherwise leave it installed-but-disabled with a hint.
if [[ "$WITH_INDEXER" -eq 1 ]]; then
    if systemctl list-unit-files 2>/dev/null | grep -q '^ollama\.service'; then
        log "enabling + starting ${INDEXER} (Ollama detected)"
        $SUDO systemctl enable --now "$INDEXER" >/dev/null 2>&1 || $SUDO systemctl start "$INDEXER" || \
            err "indexer failed to start — check 'systemctl status ${INDEXER}' (corpus at ${PREFIX}/docs?)."
    else
        err "indexer installed but NOT enabled: ollama.service was not found. The indexer embeds via Ollama."
        err "install/run Ollama, drop docs into ${PREFIX}/docs, then: sudo systemctl enable --now ${INDEXER}"
    fi
fi

# ── 4. Verify (the real pass/fail: an actual 200 from the Service /health) ─────
log "waiting for ${SERVICE} to report healthy at ${HEALTH_URL} ..."
if wait_health; then
    log "kgsm-assistant Service is up and healthy ✓   (CLI: kgsm-assistant --help)"
    [[ "$ENV_FRESH" -eq 1 ]] && err "NOTE: ${ENV_FILE} was just created from the template — set the Discord creds + Assistant__Confirmation__Key, then: sudo systemctl restart ${SERVICE}"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but ${HEALTH_URL} did not return 200 within ${HEALTH_TRIES}s. Recent logs:"
    $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
