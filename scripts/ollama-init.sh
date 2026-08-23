#!/bin/sh
# Entrypoint for the Ollama container in GWS Suite.
# Starts the Ollama server, then pulls the required models if they are not
# already cached in the persistent volume. On subsequent restarts the pull
# commands are near-instant because Ollama skips files it already has.
set -e

ollama serve &
SERVE_PID=$!

echo "[ollama-init] Waiting for Ollama server to be ready..."
until ollama list > /dev/null 2>&1; do
  sleep 3
done
echo "[ollama-init] Server is ready."

echo "[ollama-init] Synchronizing the canonical GWS model set..."
while IFS= read -r model || [ -n "$model" ]; do
  case "$model" in
    ""|\#*) continue ;;
  esac
  echo "[ollama-init] Pulling $model..."
  ollama pull "$model"
done < /ollama-profiles/required-models.txt

echo "[ollama-init] Creating the SentinelGPT profile..."
ollama create sentinelgpt -f /ollama-profiles/SentinelGPT.Modelfile

echo "[ollama-init] All models ready. GWS Suite is good to go."

wait "$SERVE_PID"
