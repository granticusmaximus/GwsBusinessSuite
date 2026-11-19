#!/usr/bin/env bash
# Sets up (or repairs) Ollama on the AI droplet, gws-ai: 4 vCPU, 8 GB RAM, no GPU (19-11-26).
# Safe to run again; each run makes the droplet match this file.
#
#   scp scripts/setup-ai-droplet.sh ollama/SentinelGPT.Modelfile root@<ai-droplet>:/root/gws-ai/
#   ssh root@<ai-droplet> 'bash /root/gws-ai/setup-ai-droplet.sh <ai-private-ip> [--prune]'
#
# What it fixes. On 19-11-26 Ollama answered nothing for hours: llama3.2 generated 0.1 tokens/s
# (normal is 10-20) because the service's MemoryHigh=5G sat below the model's real footprint
# (~6 GB with its repacked weights and mapped file), so the kernel throttled it 1.47 million times
# and pushed it into a full 4 GB swap. The web app's readiness check then failed and a deploy and
# its rollback were both refused.
#   1. Limits: no MemoryHigh (it throttles instead of failing), one hard MemoryMax of 7G, at most
#      512M of swap for Ollama (a model in swap is too slow to be useful; better to restart), and
#      vm.swappiness=10 so the rest of the box doesn't page the model out.
#   2. Models: only what fits in RAM. llama3.2 (chat, summaries), embeddinggemma (World News
#      headlines), and sentinelgpt rebuilt from the repo's profile on llama3.2 with an 8K
#      context. --prune removes anything else (gemma4 is 6.3 GB on an 8 GB box).
#      The web app sends the same context with every request (ContentStudio__ContextLength in
#      docker-compose.yml, 8192): a request with a different num_ctx makes Ollama reload the model.
#      Change all three together.
#   3. A watchdog on the droplet itself (the web app's watchdog only restarts a local container):
#      every minute it asks Ollama for its version; three misses in a row, or five minutes of
#      heavy memory stalls, restart it. At most one restart per 10 minutes, logged as
#      gws-ollama-watchdog in the journal.
set -euo pipefail

HOST_IP="${1:?usage: setup-ai-droplet.sh <ai-private-ip> [--prune]}"
PRUNE="${2:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
MODELFILE="$HERE/SentinelGPT.Modelfile"
OLLAMA_URL="http://$HOST_IP:11434"
KEEP_MODELS="llama3.2 embeddinggemma sentinelgpt"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

[ "$(id -u)" -eq 0 ] || { echo "Run as root." >&2; exit 1; }
[ -f "$MODELFILE" ] || { echo "Copy ollama/SentinelGPT.Modelfile next to this script first." >&2; exit 1; }
command -v ollama >/dev/null || { echo "Ollama isn't installed (curl -fsSL https://ollama.com/install.sh | sh)." >&2; exit 1; }

echo "== 1. Service limits"
DROPIN=/etc/systemd/system/ollama.service.d/gws.conf
mkdir -p "$(dirname "$DROPIN")"
[ -f "$DROPIN" ] && cp "$DROPIN" "$DROPIN.bak-$STAMP"
cat > "$DROPIN" <<EOF
[Service]
Environment=OLLAMA_HOST=$HOST_IP:11434
Environment=OLLAMA_KEEP_ALIVE=-1
Environment=OLLAMA_NUM_PARALLEL=1
Environment=OLLAMA_MAX_LOADED_MODELS=2
Environment=OLLAMA_CONTEXT_LENGTH=8192
MemoryHigh=infinity
MemoryMax=7G
MemorySwapMax=512M
CPUQuota=350%
TasksMax=512
OOMPolicy=stop
Restart=always
RestartSec=3
EOF
echo "vm.swappiness=10" > /etc/sysctl.d/99-gws-ai.conf
sysctl -q --system
systemctl daemon-reload
systemctl restart ollama
for _ in $(seq 1 30); do curl -sf -m 5 "$OLLAMA_URL/api/version" >/dev/null && break; sleep 2; done
curl -sf -m 5 "$OLLAMA_URL/api/version" >/dev/null || { echo "Ollama didn't come back after the restart." >&2; exit 1; }
export OLLAMA_HOST="$HOST_IP:11434"

echo "== 2. Models"
ollama pull llama3.2
ollama pull embeddinggemma
PROFILE="/tmp/SentinelGPT.gws-ai.Modelfile"
sed -e 's/^FROM .*/FROM llama3.2/' -e 's/^PARAMETER num_ctx .*/PARAMETER num_ctx 8192/' "$MODELFILE" > "$PROFILE"
ollama create sentinelgpt -f "$PROFILE"
rm -f "$PROFILE"
if [ "$PRUNE" = "--prune" ]; then
  ollama list | tail -n +2 | awk '{print $1}' | while IFS= read -r installed; do
    base="${installed%:latest}"
    case " $KEEP_MODELS " in *" $base "*) continue ;; esac
    echo "Removing $installed (doesn't fit this droplet's memory)"
    ollama rm "$installed" || true
  done
fi
# Loads the everyday model now, so the first request after setup doesn't wait for it.
curl -sf -m 300 "$OLLAMA_URL/api/generate" -d '{"model":"llama3.2","keep_alive":-1}' >/dev/null || true

echo "== 3. Watchdog"
cat > /usr/local/bin/gws-ollama-watchdog <<EOF
#!/usr/bin/env bash
# Installed by setup-ai-droplet.sh. Restarts Ollama when it stops answering or is stalled on memory.
set -u
URL="$OLLAMA_URL/api/version"
EOF
cat >> /usr/local/bin/gws-ollama-watchdog <<'EOF'
STATE=/run/gws-ollama-watchdog
CG=/sys/fs/cgroup/system.slice/ollama.service
mkdir -p "$STATE"
count() { cat "$STATE/$1" 2>/dev/null || echo 0; }
now=$(date +%s)
last=$(count last-restart)

if curl -sf -m 10 "$URL" >/dev/null; then echo 0 > "$STATE/misses"; else echo $(( $(count misses) + 1 )) > "$STATE/misses"; fi
# "full avg60": the share of the last minute in which every Ollama task waited on memory.
full=$(awk '/^full/ { split($2, a, "="); printf "%d", a[2] }' "$CG/memory.pressure" 2>/dev/null || echo 0)
if [ "${full:-0}" -ge 10 ]; then echo $(( $(count stalls) + 1 )) > "$STATE/stalls"; else echo 0 > "$STATE/stalls"; fi

reason=""
[ "$(count misses)" -ge 3 ] && reason="no answer to 3 checks in a row"
[ "$(count stalls)" -ge 5 ] && reason="memory stalls ${full}% for 5 minutes"
if [ -n "$reason" ] && [ $(( now - last )) -ge 600 ]; then
  logger -t gws-ollama-watchdog "Restarting Ollama: $reason"
  echo "$now" > "$STATE/last-restart"
  echo 0 > "$STATE/misses"; echo 0 > "$STATE/stalls"
  systemctl restart ollama
fi
EOF
chmod 755 /usr/local/bin/gws-ollama-watchdog
cat > /etc/systemd/system/gws-ollama-watchdog.service <<'EOF'
[Unit]
Description=Restart Ollama when it stops answering (GWS)
After=ollama.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/gws-ollama-watchdog
EOF
cat > /etc/systemd/system/gws-ollama-watchdog.timer <<'EOF'
[Unit]
Description=Check Ollama every minute (GWS)

[Timer]
OnBootSec=3min
OnUnitActiveSec=1min
AccuracySec=5s

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now gws-ollama-watchdog.timer

echo "== Done"
ollama list
systemctl show ollama -p MemoryHigh -p MemoryMax -p MemorySwapMax --no-pager
systemctl list-timers gws-ollama-watchdog.timer --no-pager | head -3
