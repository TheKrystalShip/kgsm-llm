#!/usr/bin/env bash
# Chooses whether the llama.cpp chat model is resident all the time or only while it is in use.
#
#   sudo ./use-chat-mode.sh always-hot          — loaded at boot, never unloaded
#   sudo ./use-chat-mode.sh on-demand [TIMEOUT] — loaded on first request, unloaded when idle
#   ./use-chat-mode.sh status                   — what is set now
#
# The assistant's endpoint does not change either way: kgsm-llama-chat.socket owns LLAMA_CHAT_PORT
# and is listening in both modes, so nothing that talks to the model needs to know which is set.
#
# on-demand gives back ~8.7GB of VRAM and up to ~1.6GB of host RAM through idle stretches, and the
# next request after one pays a ~4.3s cold start. always-hot is the arrangement to pick when that
# pause matters more than the memory.
set -euo pipefail

ENV_FILE=/etc/kgsm-assistant/llama-server.env
MODEL_UNIT=kgsm-llama-chat.service
SOCKET_UNIT=kgsm-llama-chat.socket
PROXY_UNIT=kgsm-llama-chat-proxy.service

die() { echo "error: $*" >&2; exit 1; }

need_root() { [[ $EUID -eq 0 ]] || die "must run as root (it edits $ENV_FILE and enables units)"; }

current_timeout() {
    awk -F= '/^LLAMA_CHAT_IDLE_TIMEOUT=/{print $2}' "$ENV_FILE" 2>/dev/null | tail -1
}

set_timeout() {
    local value=$1
    if grep -q '^LLAMA_CHAT_IDLE_TIMEOUT=' "$ENV_FILE"; then
        sed -i "s|^LLAMA_CHAT_IDLE_TIMEOUT=.*|LLAMA_CHAT_IDLE_TIMEOUT=${value}|" "$ENV_FILE"
    else
        printf 'LLAMA_CHAT_IDLE_TIMEOUT=%s\n' "$value" >> "$ENV_FILE"
    fi
}

status() {
    local enabled; enabled=$(systemctl is-enabled "$MODEL_UNIT" 2>/dev/null || true)
    local active;  active=$(systemctl is-active "$MODEL_UNIT" 2>/dev/null || true)
    if [[ $enabled == enabled ]]; then
        echo "mode:          always-hot (model unit enabled — resident from boot)"
    else
        echo "mode:          on-demand (model unit disabled — loaded on first request)"
        echo "idle timeout:  $(current_timeout)"
    fi
    echo "model:         $active"
    echo "socket:        $(systemctl is-active "$SOCKET_UNIT" 2>/dev/null || true)"
    echo "proxy:         $(systemctl is-active "$PROXY_UNIT" 2>/dev/null || true)"
}

case ${1:-status} in
status)
    status
    ;;

always-hot)
    need_root
    # infinity matters even with the unit enabled: it stops the proxy exiting under the model and
    # dragging a restart out of Requires= churn on an otherwise idle host.
    set_timeout infinity
    systemctl enable "$MODEL_UNIT" >/dev/null
    systemctl enable "$SOCKET_UNIT" >/dev/null
    systemctl daemon-reload
    systemctl restart "$SOCKET_UNIT"
    systemctl start "$MODEL_UNIT"
    systemctl try-restart "$PROXY_UNIT" 2>/dev/null || true
    echo "chat model is now always-hot — resident from boot, never unloaded."
    status
    ;;

on-demand)
    need_root
    timeout=${2:-15min}
    set_timeout "$timeout"
    # Disabling is what arms StopWhenUnneeded= on the model unit: while a target still wants it, it
    # is needed by definition and would never unload no matter what the proxy does.
    systemctl disable "$MODEL_UNIT" >/dev/null
    systemctl enable "$SOCKET_UNIT" >/dev/null
    systemctl daemon-reload
    systemctl restart "$SOCKET_UNIT"
    # Stopping the model now rather than waiting for the first idle window makes the change visible
    # immediately, and the socket is already listening so nothing is refused in the meantime.
    systemctl stop "$PROXY_UNIT" 2>/dev/null || true
    systemctl stop "$MODEL_UNIT" 2>/dev/null || true
    echo "chat model is now on-demand — unloads after ${timeout} idle, ~4.3s cold start after that."
    status
    ;;

*)
    die "usage: $0 [status|always-hot|on-demand [TIMEOUT]]"
    ;;
esac
