#!/usr/bin/env bash
# Installs the two llama-server units on this host. Needs root — unlike deploy.sh, which delivers
# code into a prefix that is already yours, these are host units that decide what occupies the GPU.
#
# Idempotent and re-runnable. It does NOT enable or start anything: putting the units in place and
# choosing to run them are separate decisions, and the second one takes VRAM away from Ollama.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE=/etc/kgsm-assistant/llama-server.env
ASSISTANT_UNIT=/etc/kgsm-assistant/systemd/kgsm-assistant-service.service
UNITS=(kgsm-llama-chat.service kgsm-llama-embed.service)

[[ $EUID -eq 0 ]] || { echo "run me with sudo: sudo $0" >&2; exit 1; }

# The models are read by the same user the assistant runs as, so the units follow whatever the
# assistant's own unit was templated with rather than restating a guess.
if [[ -r $ASSISTANT_UNIT ]]; then
    RUN_USER=$(awk -F= '/^User=/{print $2}' "$ASSISTANT_UNIT" | tail -1)
    RUN_GROUP=$(awk -F= '/^Group=/{print $2}' "$ASSISTANT_UNIT" | tail -1)
fi
RUN_USER=${RUN_USER:-${SUDO_USER:-root}}
RUN_GROUP=${RUN_GROUP:-$RUN_USER}
echo "running llama-server as ${RUN_USER}:${RUN_GROUP}"

install -d -o "$RUN_USER" -g "$RUN_GROUP" -m 0755 /var/lib/llama /var/lib/llama/models

install -d -m 0755 /etc/kgsm-assistant
if [[ -e $ENV_FILE ]]; then
    echo "keeping existing $ENV_FILE"
else
    install -m 0644 "$HERE/llama-server.env.example" "$ENV_FILE"
    echo "seeded $ENV_FILE — set the model paths in it before starting anything"
fi

# systemd will not expand a variable in the executable position, so the binary is baked in here.
LLAMA_BIN=${LLAMA_SERVER_BIN:-$(command -v llama-server || true)}
[[ -x $LLAMA_BIN ]] || { echo "no llama-server found; install llama.cpp or set LLAMA_SERVER_BIN" >&2; exit 1; }
echo "llama-server binary: $LLAMA_BIN"

for unit in "${UNITS[@]}"; do
    sed -e "s/^User=.*/User=${RUN_USER}/" -e "s/^Group=.*/Group=${RUN_GROUP}/" \
        -e "s|^ExecStart=.*llama-server |ExecStart=${LLAMA_BIN} |" \
        "$HERE/$unit" > "/etc/systemd/system/$unit"
    chmod 0644 "/etc/systemd/system/$unit"
    echo "installed $unit"
done

systemctl daemon-reload

cat <<EOF

Installed, not started and not enabled. Put the GGUFs in /var/lib/llama/models and check the
paths in ${ENV_FILE}, then switch with:

    sudo ./use-backend.sh llamacpp        # or: ollama

Use that rather than starting the units by hand. It moves the units AND the assistant's
configuration together, and sets which backend comes back after a reboot — leaving both enabled
makes boot a race that Conflicts= resolves arbitrarily, and the assistant would come up pointed at
whichever one lost.
EOF
