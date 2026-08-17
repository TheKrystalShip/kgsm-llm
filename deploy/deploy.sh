#!/usr/bin/env bash
#
# deploy.sh — build + deploy the kgsm-llm assistant. Fully headless: no sudo, no prompts.
#
#   ./deploy/deploy.sh                 # Service (HTTP/SSE) + CLI (on PATH)
#   ./deploy/deploy.sh --with-indexer  # also build + start the RAG indexer (needs Ollama)
#
# Assumes deploy/setup.sh has provisioned this host (prefix + state dir owned by you, the units
# symlinked out of a directory you own, the CLI symlink on PATH, polkit grant in place). If it
# has not, this script says so and stops before building.
#
# Three artifacts (see docs/DEPLOYMENT.md), all published as YOU:
#   * Service  — framework-dependent single file → /opt/kgsm-assistant/service  (systemd, :5180)
#   * CLI      — framework-dependent single file → /opt/kgsm-assistant/cli (symlinked on PATH)
#   * Indexer  — Native-AOT single file          → /opt/kgsm-assistant/indexer (opt-in; needs Ollama)
#
# Service + CLI need the .NET 10 ASP.NET Core runtime on the host (checked up front). kgsm-lib is
# consumed as the packed NuGet TheKrystalShip.KGSM.Lib from the org's GitHub Packages feed
# (nuget.config), so this repo builds standalone. The env file /etc/kgsm-assistant/service.env is
# setup.sh's business and is never touched here — your secrets survive every redeploy.
#
# Knobs: RID, ASSISTANT_URL, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

WITH_INDEXER=0
[[ "${1:-}" == "--with-indexer" ]] && WITH_INDEXER=1

SVC_PROJ="$REPO_DIR/TheKrystalShip.Kgsm.Assistant.Service/TheKrystalShip.Kgsm.Assistant.Service.csproj"
CLI_PROJ="$REPO_DIR/TheKrystalShip.Kgsm.Assistant.Cli/TheKrystalShip.Kgsm.Assistant.Cli.csproj"
IDX_PROJ="$REPO_DIR/TheKrystalShip.Rag.Indexer/TheKrystalShip.Rag.Indexer.csproj"
PUB="$PUBLISH_DIR"
INDEXER="kgsm-rag-indexer.service"
RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$SVC_PROJ" ]] || { err "project not found: $SVC_PROJ"; exit 1; }
# kgsm-lib restores as TheKrystalShip.KGSM.Lib from the org's GitHub Packages feed (nuget.config),
# so no sibling checkout has to be present for this build to succeed.
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

# ── 2. Refresh the units if they changed (we own the files; systemd reads them via symlinks) ──
install_units_unprivileged

# ── 2b. Publish the leaf config descriptor ────────────────────────────────────
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 2c. Publish the command manifest ──────────────────────────────────────────
# The catalog of commands the Control Panel lists, in a subdirectory of the same discovery tree so
# it cannot be mistaken for a config descriptor by the scan that reads those. Written by the build
# from the binary it just produced; installed here, unprivileged, because the parent directory is
# ours. The panel reads it by scanning a directory, so this leaf's command surface becomes
# documented by landing one file — with no rebuild in kgsm-api.
install_command_manifest() {
    local src="${REPO_DIR}/deploy/${PROJECT}.commands.json"
    [[ -f "$src" ]] || { warn "no command manifest at ${src} — the Control Panel will list no commands."; return 0; }

    local dir="${LEAF_DESCRIPTOR_DIR}/commands"
    mkdir -p "$dir"

    local dst="${dir}/${LEAF_ID}.json"
    if ! cmp -s "$src" "$dst"; then
        log "command manifest changed → ${dst}"
        install -m 0644 "$src" "$dst"
    fi
}
install_command_manifest

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
sysctl_do stop "$SERVICE" || true
STOPPED=1
[[ "$WITH_INDEXER" -eq 1 ]] && { sysctl_do stop "$INDEXER" || true; }

log "syncing publish trees → ${PREFIX}"
# wwwroot belongs to whoever publishes the web client (kgsm-web's deploy-assistant.sh), not to this
# repo's publish tree, so --delete must not reach into it: a sibling's artifact living under our
# prefix is still a sibling's artifact. setup.sh creates the directory for the same reason.
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' --exclude='/wwwroot/' \
    "$PUB/service/" "$PREFIX/service/"
# ⚠ The CLI resolves its conversation database to a file BESIDE its binary — it has no state
# directory of its own, unlike the service — so the database lives inside the tree being synced.
# Excluded from --delete because it is state, not an artifact: without this, every deploy destroys
# the CLI's whole conversation history and everything the assistant has remembered about anyone who
# talks to it there. The -wal/-shm siblings go with it; deleting those alone corrupts an open store.
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' \
    --exclude='/conversations.db' --exclude='/conversations.db-wal' --exclude='/conversations.db-shm' \
    "$PUB/cli/" "$PREFIX/cli/"

# The prompts and tool definitions the assistant runs on. They are SHIPPED ARTIFACTS, not state:
# --delete and no exceptions, so what is installed is exactly what this commit says. That is also
# why they live under the prefix rather than the state directory — the state directory is for what
# must SURVIVE a deploy (the conversation database, the RAG index), and these must not.
#
# ⚠ A local edit here is overwritten by the next deploy. That is the intended loop: tune the file on
# the running host, confirm it, then paste the wording back into deploy/prompts/ so it ships. The
# deploy is the commit.
log "installing prompts + tool definitions → ${PREFIX}/prompts"
rsync -a --delete "$REPO_DIR/deploy/prompts/" "$PREFIX/prompts/"
[[ "$WITH_INDEXER" -eq 1 ]] && rsync -a --delete "$PUB/indexer/" "$PREFIX/indexer/"

# The CLI symlink on PATH points at a stable path inside the prefix, so replacing the binary
# behind it needs no privilege and no re-linking. setup.sh owns the link itself.

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# Indexer: start it, and let systemd answer whether it can run. Its unit declares
# Requires=ollama.service, so a host without Ollama fails the start there — the same answer this
# script could work out for itself, without carrying a second copy of the dependency that has to
# be kept in step with the unit. This script stops the indexer before syncing, so anything that
# decides NOT to start it again leaves the host worse than it found it; the start is therefore
# unconditional and a failure is reported rather than predicted. Enabling it at boot is
# setup.sh's call, and setup deliberately does not — it is opt-in.
if [[ "$WITH_INDEXER" -eq 1 ]]; then
    log "starting ${INDEXER}"
    if ! sysctl_do start "$INDEXER"; then
        warn "indexer installed but NOT started. Its unit requires ollama.service, so this also"
        warn "fails when Ollama is absent or not running — check 'systemctl status ${INDEXER}'."
        warn "corpus lives at ${PREFIX}/docs; start it later with: systemctl start ${INDEXER}"
    fi
fi

# ── 4. Verify (the real pass/fail: an actual 200 from the Service /health) ─────
log "waiting for ${SERVICE} to report healthy at ${ASSISTANT_URL}/health ..."
if wait_health; then
    log "kgsm-assistant Service is up and healthy ✓   (CLI: kgsm-assistant --help)"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but ${ASSISTANT_URL}/health did not return 200 within ${HEALTH_TRIES}s. Recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
