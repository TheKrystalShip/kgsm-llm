#!/usr/bin/env bash
# Switches the assistant between its two inference backends, units and configuration together.
#
#   sudo ./use-backend.sh ollama     — Ollama serves chat and embeddings
#   sudo ./use-backend.sh llamacpp   — llama.cpp serves chat and embeddings
#   ./use-backend.sh status          — report what is running and what the assistant is pointed at
#
# The two halves have to move together: the units decide what holds the GPU, and service.env decides
# what the assistant talks to. Setting one without the other is how the assistant ends up pointed at
# a port nothing is listening on.
set -euo pipefail

ENV_FILE=/etc/kgsm-assistant/service.env
# The indexer is a separate unit taking CLI flags, so pointing the assistant at a backend does not
# move it. Left behind it rebuilds against the OTHER server — or fails, which is the good case.
INDEXER_ENV=/etc/kgsm-assistant/rag-indexer.env
ASSISTANT_UNIT=kgsm-assistant-service.service
# The socket is what the assistant connects to and it must move with the backend: left enabled
# under Ollama it would keep listening on the chat port and activate a second model server
# behind Ollama's back. The proxy follows the socket and needs no separate handling.
LLAMA_UNITS=(kgsm-llama-chat.socket kgsm-llama-chat-proxy.service kgsm-llama-chat.service kgsm-llama-embed.service)
# Ollama's whole boot-time surface, not just the daemon. ollama-preload.service belongs here because
# disabling a unit does not keep it from starting: enablement decides only whether a TARGET wants it,
# and a Requires= from some other enabled unit pulls a disabled ollama.service up anyway. Left behind
# on a llama.cpp host it loads a second copy of the model into the same VRAM at every boot and pins it
# (keep_alive:-1), until the chat unit's Conflicts= happens to evict it.
OLLAMA_UNITS=(ollama.service ollama-preload.service)
# What switching TO llama.cpp turns on. The chat model is not here on purpose — the socket activates
# it, and its enabled state is the residency mode use-chat-mode.sh owns.
LLAMA_ACTIVATION_UNITS=(kgsm-llama-chat.socket kgsm-llama-embed.service)
LLAMA_ENV=/etc/kgsm-assistant/llama-server.env

usage() { sed -n '2,9p' "$0" | sed 's/^# \?//'; exit "${1:-1}"; }

status() {
    echo "units:"
    for u in "${OLLAMA_UNITS[@]}" "${LLAMA_UNITS[@]}"; do
        # is-active prints the state AND exits non-zero when it is not active, so the status is
        # taken from stdout and the exit code deliberately ignored.
        local state boot
        state=$(systemctl is-active "$u" 2>/dev/null) || true
        boot=$(systemctl is-enabled "$u" 2>/dev/null) || true
        printf '  %-28s %-10s at boot: %s\n' "$u" "${state:-unknown}" "${boot:-unknown}"
    done
    echo "assistant is pointed at:"
    if [[ -r $ENV_FILE ]]; then
        grep -E '^(Llm|Rag)__(Provider|Endpoint)=' "$ENV_FILE" | sed 's/^/  /' || echo "  (defaults — nothing set)"
    else
        echo "  $ENV_FILE not readable (try sudo)"
    fi
    local astate; astate=$(systemctl is-active "$ASSISTANT_UNIT" 2>/dev/null) || true
    echo "assistant service: ${astate:-unknown}"
}

# Replaces a KEY=value line, or appends it when the key is absent. Idempotent.
set_key() {
    local key=$1 value=$2
    if grep -qE "^${key}=" "$ENV_FILE"; then
        sed -i "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
    else
        printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
    fi
}

# The index records the embedding model's NAME, which does not change when the server behind it
# does, so a rebuilt index can silently hold the previous embedder's vectors. Restarting the indexer
# here only re-points it; the vectors are the operator's call, which is what the warning is for.
write_indexer_env() {
    printf 'RAG_EMBED_PROVIDER=%s\nRAG_EMBED_ENDPOINT=%s\n' "$1" "$2" > "$INDEXER_ENV"
    chmod 0644 "$INDEXER_ENV"
    systemctl try-restart kgsm-rag-indexer.service 2>/dev/null || true
}

require_root() { [[ $EUID -eq 0 ]] || { echo "run me with sudo: sudo $0 $*" >&2; exit 1; }; }

case "${1:-}" in
status) status; exit 0 ;;

ollama)
    require_root "$@"
    systemctl stop "${LLAMA_UNITS[@]}" 2>/dev/null || true
    # Boot state follows the choice. Leaving both backends enabled makes a reboot a race that
    # Conflicts= resolves arbitrarily, and the assistant would come up pointed at the loser.
    systemctl disable "${LLAMA_UNITS[@]}" 2>/dev/null || true
    # The llamacpp switch masks it, so it has to be released before it can be enabled or started.
    systemctl unmask ollama.service 2>/dev/null || true
    systemctl enable ollama.service 2>/dev/null || true
    systemctl start ollama.service
    set_key Llm__Provider Ollama
    set_key Llm__Endpoint http://localhost:11434
    set_key Rag__Provider Ollama
    set_key Rag__Endpoint http://localhost:11434
    write_indexer_env Ollama http://localhost:11434
    systemctl restart "$ASSISTANT_UNIT"
    echo "switched to Ollama"
    # Residency is the operator's call on both backends, so it is left off rather than turned on
    # here — the same reason the llamacpp branch leaves the chat MODEL unit to use-chat-mode.sh.
    # Without it Ollama loads the model on the first request instead of at boot.
    if systemctl list-unit-files ollama-preload.service &>/dev/null; then
        echo
        echo "note: ollama-preload.service is left disabled — the model loads on the first request."
        echo "  To pin it into VRAM at boot instead: systemctl enable --now ollama-preload.service"
    fi
    ;;

llamacpp)
    require_root "$@"
    [[ -r $LLAMA_ENV ]] || { echo "$LLAMA_ENV missing — run install.sh first" >&2; exit 1; }
    # Starting the chat unit stops Ollama on its own (Conflicts), but doing it here too keeps the
    # order explicit rather than leaving it to unit ordering.
    systemctl stop "${OLLAMA_UNITS[@]}" 2>/dev/null || true
    systemctl disable "${OLLAMA_UNITS[@]}" 2>/dev/null || true
    # Disabling is not sufficient on its own: any unit that Requires= ollama.service starts it
    # whatever its enabled state, which is how a disabled Ollama comes up at boot and takes the VRAM
    # the chat model is about to want. Masked it cannot be pulled in at all, and whatever asked for
    # it fails loudly instead of quietly winning the GPU.
    systemctl mask ollama.service 2>/dev/null || true
    # The chat MODEL unit is deliberately absent from both lists: whether it is enabled is the
    # always-hot/on-demand choice and belongs to use-chat-mode.sh. Enabling it here would pin every
    # host to always-hot on each backend switch. The socket is what has to be up — it listens on the
    # assistant's endpoint and brings the model in on the first request either way.
    systemctl enable "${LLAMA_ACTIVATION_UNITS[@]}" 2>/dev/null || true
    systemctl start "${LLAMA_ACTIVATION_UNITS[@]}"
    # shellcheck disable=SC1090
    chat_port=$(awk -F= '/^LLAMA_CHAT_PORT=/{print $2}' "$LLAMA_ENV")
    embed_port=$(awk -F= '/^LLAMA_EMBED_PORT=/{print $2}' "$LLAMA_ENV")
    ctx=$(awk -F= '/^LLAMA_CHAT_CTX=/{print $2}' "$LLAMA_ENV")
    set_key Llm__Provider LlamaCpp
    set_key Llm__Endpoint "http://127.0.0.1:${chat_port:-8081}"
    # The server fixes the context window at launch, so the assistant is told the same number rather
    # than a default that would silently measure token usage against the wrong denominator.
    set_key Llm__ContextWindow "${ctx:-32768}"
    set_key Rag__Provider LlamaCpp
    set_key Rag__Endpoint "http://127.0.0.1:${embed_port:-8082}"
    write_indexer_env LlamaCpp "http://127.0.0.1:${embed_port:-8082}"
    systemctl restart "$ASSISTANT_UNIT"
    echo "switched to llama.cpp"
    echo
    echo "⚠ the RAG index holds vectors from whichever embedder built it. Switching the embedding"
    echo "  backend changes the vectors without changing the index's header, so retrieval degrades"
    echo "  quietly rather than failing. Rebuild it:  systemctl start kgsm-rag-indexer"
    ;;

*) usage 0 ;;
esac

echo
status
