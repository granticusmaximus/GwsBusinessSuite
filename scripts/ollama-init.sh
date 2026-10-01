#!/bin/sh
# Entrypoint for the Ollama container in GWS Suite.
# Starts the Ollama server, then pulls the required models if they are not
# already cached in the persistent volume. On subsequent restarts the pull
# commands are near-instant because Ollama skips files it already has.
#
# Optional environment (all set by docker-compose.yml for the light production droplet; when
# unset, this behaves exactly as before - the full required-models.txt set and the stock profile):
#   OLLAMA_MODEL_MANIFEST       model list to pull (default: required-models.txt)
#   SENTINELGPT_BASE_MODEL      rebuild the sentinelgpt profile on this base instead of the
#                               Modelfile's own FROM line (same system prompt either way)
#   SENTINELGPT_NUM_CTX         override the profile's num_ctx (smaller = less RAM per request)
#   OLLAMA_PRUNE_UNLISTED=true  remove installed models that are not in the manifest, so an
#                               old heavy model can't be loaded by anything that discovers
#                               installed models dynamically
set -e

MANIFEST="${OLLAMA_MODEL_MANIFEST:-/ollama-profiles/required-models.txt}"
PROFILE="/ollama-profiles/SentinelGPT.Modelfile"

ollama serve &
SERVE_PID=$!

echo "[ollama-init] Waiting for Ollama server to be ready..."
until ollama list > /dev/null 2>&1; do
  sleep 3
done
echo "[ollama-init] Server is ready."

echo "[ollama-init] Synchronizing the model set from $MANIFEST..."
while IFS= read -r model || [ -n "$model" ]; do
  case "$model" in
    ""|\#*) continue ;;
  esac
  echo "[ollama-init] Pulling $model..."
  ollama pull "$model"
done < "$MANIFEST"

if [ -n "${SENTINELGPT_BASE_MODEL:-}" ] || [ -n "${SENTINELGPT_NUM_CTX:-}" ]; then
  GENERATED_PROFILE="/tmp/SentinelGPT.server.Modelfile"
  cp "$PROFILE" "$GENERATED_PROFILE"
  if [ -n "${SENTINELGPT_BASE_MODEL:-}" ]; then
    sed -i "s/^FROM .*/FROM $SENTINELGPT_BASE_MODEL/" "$GENERATED_PROFILE"
  fi
  if [ -n "${SENTINELGPT_NUM_CTX:-}" ]; then
    sed -i "s/^PARAMETER num_ctx .*/PARAMETER num_ctx $SENTINELGPT_NUM_CTX/" "$GENERATED_PROFILE"
  fi
  PROFILE="$GENERATED_PROFILE"
fi

echo "[ollama-init] Creating the SentinelGPT profile ($(grep '^FROM ' "$PROFILE"))..."
ollama create sentinelgpt -f "$PROFILE"

if [ "${OLLAMA_PRUNE_UNLISTED:-false}" = "true" ]; then
  echo "[ollama-init] Removing models that are not in $MANIFEST..."
  ollama list | tail -n +2 | awk '{print $1}' | while IFS= read -r installed; do
    base="${installed%:latest}"
    [ "$base" = "sentinelgpt" ] && continue
    if ! grep -qxF "$base" "$MANIFEST" && ! grep -qxF "$installed" "$MANIFEST"; then
      echo "[ollama-init] Removing $installed..."
      ollama rm "$installed" || true
    fi
  done
fi

echo "[ollama-init] All models ready. GWS Suite is good to go."

wait "$SERVE_PID"
