#!/usr/bin/env bash
# Key-less web search for SentinelGPT (13-11-26): runs SearXNG (open-source metasearch, no API key)
# in Docker on the AI droplet, listening only on its private address and reachable only from the
# web droplet. Run on the AI droplet as a sudo user:
#
#   sudo bash setup-searxng.sh <ai-droplet-private-ip> <web-droplet-private-ip>
#
# Then on the web droplet add SEARXNG_URL=http://<ai-droplet-private-ip>:8888 to /opt/gwssuite/.env
# and let the next deploy (or GWS_IMAGE_TAG=<running sha> docker compose up -d --no-build gwssuite)
# pick it up. Re-running the script keeps the existing secret and settings.
set -euo pipefail
BIND_IP="${1:?private IP of this (AI) droplet}"
WEB_IP="${2:?private IP of the web droplet}"
PORT=8888
CONF=/etc/searxng

if ! command -v docker >/dev/null 2>&1; then
  apt-get update -qq && apt-get install -y -qq docker.io
  systemctl enable --now docker
fi

install -d -m 755 "$CONF"
if [ ! -f "$CONF/settings.yml" ]; then
  SECRET="$(openssl rand -hex 32)"
  cat > "$CONF/settings.yml" <<YAML
use_default_settings: true
general:
  instance_name: "GWS search"
server:
  secret_key: "$SECRET"
  # Private instance: only the web droplet can reach it, so no rate limiter or public features.
  limiter: false
  public_instance: false
  image_proxy: false
search:
  safe_search: 1
  formats:
    - html
    - json
YAML
  chmod 644 "$CONF/settings.yml"
fi

docker rm -f searxng >/dev/null 2>&1 || true
docker run -d --name searxng --restart unless-stopped \
  -p "$BIND_IP:$PORT:8080" \
  -v "$CONF:/etc/searxng" \
  --memory 512m --cpus 1 \
  docker.io/searxng/searxng:latest

if command -v ufw >/dev/null 2>&1; then
  ufw allow from "$WEB_IP" to "$BIND_IP" port "$PORT" proto tcp comment 'SearXNG: GWS web droplet only'
fi

for _ in $(seq 1 30); do
  if curl -fsS --max-time 5 "http://$BIND_IP:$PORT/search?q=test&format=json" >/dev/null; then
    echo "SearXNG is answering on http://$BIND_IP:$PORT"; exit 0
  fi
  sleep 2
done
echo "SearXNG didn't answer; check: docker logs searxng" >&2
exit 1
