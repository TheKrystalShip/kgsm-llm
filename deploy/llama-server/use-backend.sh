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
ASSISTANT_UNIT=kgsm-assistant-service.service
LLAMA_UNITS=(kgsm-llama-chat.service kgsm-llama-embed.service)
LLAMA_ENV=/etc/kgsm-assistant/llama-server.env

usage() { sed -n '2,9p' "$0" | sed 's/^# \?//'; exit "${1:-1}"; }

status() {
    echo "units:"
    for u in ollama.service "${LLAMA_UNITS[@]}"; do
        # is-active prints the state AND exits non-zero when it is not active, so the status is
        # taken from stdout and the exit code deliberately ignored.
        local state; state=$(systemctl is-active "$u" 2>/dev/null) || true
        printf '  %-28s %s\n' "$u" "${state:-unknown}"
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

require_root() { [[ $EUID -eq 0 ]] || { echo "run me with sudo: sudo $0 $*" >&2; exit 1; }; }

case "${1:-}" in
status) status; exit 0 ;;

ollama)
    require_root "$@"
    systemctl stop "${LLAMA_UNITS[@]}" 2>/dev/null || true
    systemctl start ollama.service
    set_key Llm__Provider Ollama
    set_key Llm__Endpoint http://localhost:11434
    set_key Rag__Provider Ollama
    set_key Rag__Endpoint http://localhost:11434
    systemctl restart "$ASSISTANT_UNIT"
    echo "switched to Ollama"
    ;;

llamacpp)
    require_root "$@"
    [[ -r $LLAMA_ENV ]] || { echo "$LLAMA_ENV missing — run install.sh first" >&2; exit 1; }
    # Starting the chat unit stops Ollama on its own (Conflicts), but doing it here too keeps the
    # order explicit rather than leaving it to unit ordering.
    systemctl stop ollama.service 2>/dev/null || true
    systemctl start "${LLAMA_UNITS[@]}"
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
