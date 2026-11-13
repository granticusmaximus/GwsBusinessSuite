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
# Engines that refuse a datacenter address (a CAPTCHA) stay off: refusals are respected.
engines:
  - name: duckduckgo
    disabled: true
YAML
  chmod 644 "$CONF/settings.yml"
fi

docker rm -f searxng >/dev/null 2>&1 || true
docker run -d --name searxng --restart unless-stopped \
  -p "$BIND_IP:$PORT:8080" \
  -v "$CONF:/etc/searxng" \
  --memory 512m --cpus 1 \
  docker.io/searxng/searxng:latest

# Docker's published ports bypass UFW, so the allow-list lives in Docker's own DOCKER-USER chain:
# only the web droplet may open connections to containers; everything else from outside is
# dropped. Persisted in UFW's after.rules so it survives a reboot.
PRIV_IF="$(ip -o -4 addr show | awk -v ip="$BIND_IP" '$4 ~ "^"ip"/" {print $2; exit}')"
PUB_IF="$(ip route show default | awk '{print $5; exit}')"
apply_rules() {
  iptables -F DOCKER-USER
  iptables -A DOCKER-USER -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN
  iptables -A DOCKER-USER -i "$PRIV_IF" -s "$WEB_IP" -j RETURN
  iptables -A DOCKER-USER -i "$PRIV_IF" -j DROP
  iptables -A DOCKER-USER -i "$PUB_IF" -j DROP
}
apply_rules
if ! grep -q "GWS SearXNG" /etc/ufw/after.rules 2>/dev/null; then
  cp -p /etc/ufw/after.rules "/etc/ufw/after.rules.bak-$(date -u +%Y%m%dT%H%M%SZ)"
  cat >> /etc/ufw/after.rules <<RULES

# GWS SearXNG (setup-searxng.sh): only the web droplet reaches containers.
*filter
:DOCKER-USER - [0:0]
-A DOCKER-USER -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN
-A DOCKER-USER -i $PRIV_IF -s $WEB_IP -j RETURN
-A DOCKER-USER -i $PRIV_IF -j DROP
-A DOCKER-USER -i $PUB_IF -j DROP
COMMIT
RULES
fi

for _ in $(seq 1 30); do
  if curl -fsS --max-time 5 "http://$BIND_IP:$PORT/search?q=test&format=json" >/dev/null; then
    echo "SearXNG is answering on http://$BIND_IP:$PORT"; exit 0
  fi
  sleep 2
done
echo "SearXNG didn't answer; check: docker logs searxng" >&2
exit 1
