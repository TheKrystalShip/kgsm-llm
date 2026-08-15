#!/usr/bin/env bash
# Downloads the GGUFs llama-server needs into /var/lib/llama/models. Skips anything already there,
# so it is safe to re-run and cheap when nothing is missing.
#
# These come from the models' own GGUF repositories, NOT from an Ollama blob store: Ollama's blobs
# are not portable. Its embeddinggemma blob fails to load in mainline llama.cpp with "wrong number
# of tensors; expected 316, got 314", even though Ollama's own bundled server reads it.
#
#   usage: fetch-models.sh [--dir /var/lib/llama/models]
set -euo pipefail

DIR=/var/lib/llama/models
[[ ${1:-} == --dir ]] && DIR=${2:?--dir needs a path}

HF=https://huggingface.co
# name|url  — kept in one list so adding a model is one line and the loop stays dumb.
MODELS=(
  "gemma4-12b-q4_k_m.gguf|$HF/unsloth/gemma-4-12B-it-qat-GGUF/resolve/main/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf"
  "gemma4-12b-mmproj.gguf|$HF/unsloth/gemma-4-12B-it-qat-GGUF/resolve/main/mmproj-F16.gguf"
  "embeddinggemma-300M-Q8_0.gguf|$HF/ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf"
  "mtp-gemma-4-12b-q8_0.gguf|$HF/unsloth/gemma-4-12B-it-qat-GGUF/resolve/main/MTP/mtp-gemma-4-12B-it-Q8_0.gguf"
)

mkdir -p "$DIR"
echo "models → $DIR"

for entry in "${MODELS[@]}"; do
    name=${entry%%|*}
    url=${entry#*|}
    target="$DIR/$name"
    if [[ -s $target ]]; then
        printf '  have  %-32s %s\n' "$name" "$(du -h "$target" | cut -f1)"
        continue
    fi
    printf '  fetch %-32s\n' "$name"
    # Download beside the target and rename, so an interrupted run never leaves a half file that the
    # next run would treat as present.
    curl -fL --progress-bar -o "$target.part" "$url"
    mv "$target.part" "$target"
    printf '        %s\n' "$(du -h "$target" | cut -f1)"
done

echo
echo "done. Sizes are ~7.5 GB in total; the draft head is the small one and only MTP uses it."
