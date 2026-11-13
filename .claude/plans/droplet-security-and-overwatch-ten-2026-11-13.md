# Droplet security and the ten Overwatch features (13-11-26)

Grant (13-11-26): secure both droplets and build the ten Overwatch features. Constraint: the
app's existing users keep exactly the access they have, and their data stays theirs. So no
changes at the app's front door (no Cloudflare Access, no new sign-in steps) and no data moves.

## Phase 1: droplets (done 13-11-26, with Grant's permission)

Web droplet:
- FOUND: seven side-app containers (ports 3001, 3011, 3040, 3041, 5175, 5177, 5180) answered
  directly from the internet; Docker's published ports bypass UFW. FIXED: DOCKER-USER drops new
  connections from eth0 (v4 + v6), persisted in /etc/ufw/after.rules and after6.rules (backups
  *.bak-<time>). Checked: all ports closed from outside; every hostname through the Cloudflare
  tunnel still answers (admin 302, grantwatson.dev, wishlist, campcanaan 200); containers still
  reach the internet.
- UFW: removed the unused 80/tcp allow (the app listens on 127.0.0.1 only).
- SSH: /etc/ssh/sshd_config.d/10-gws-hardening.conf: root by key only, no passwords, no X11 or
  agent forwarding, MaxAuthTries 3. Fresh key login checked; password login refused.
- Already fine: unattended-upgrades on; passwords already off.
- NOT changed (Grant to decide): the Docker socket gives the app (Admin "Docker Management":
  start/stop/remove/recreate/exec) root-equivalent control of the host. Narrowing it with a
  socket proxy would remove those Admin actions, which the constraint rules out. The compose
  comment that called it read-only was wrong and is corrected.
- NOT changed: GitHub deploy reaches SSH from changing runner addresses, so port 22 stays open
  (key-only).

AI droplet:
- Already fine (Codex 08-11): key-only SSH, fail2ban, unattended-upgrades, Ollama on the private
  address only, UFW allows 11434 from the web droplet only.
- SearXNG installed (scripts/setup-searxng.sh): Docker, 10.116.0.3:8888 only, DOCKER-USER lets
  only the web droplet in, DuckDuckGo off (it answers datacenters with a CAPTCHA). Checked from
  the web droplet (results), closed from the internet. Web .env has SEARXNG_URL (takes effect with
  the deploy that carries the SearXNG code).
- Left for Grant: the codex-audit account (passwordless sudo) is how the assistants reach this
  droplet; remove or lock it when that access isn't wanted.

## Phase 2: the ten Overwatch features

| # | Feature | State |
| --- | --- | --- |
| 1 | Time slider for every layer (72 h replay + what changed) | |
| 2 | Layer health panel | |
| 3 | Satellite scenes on click (Earth Search STAC, before/after) | done via Planetary Computer (STAC + tiles, CORS); GIBS HLS too coarse in 3857 |
| 4 | Night lights (NASA Black Marble via GIBS) | done (web + Mac map) |
| 5 | Chokepoints and ports (IMF PortWatch) | done (ChokepointService, SEA group, Mac map) |
| 6 | Flight anomalies (emergency squawks, holding, diversions) | squawks done; holding/diversions need track history (not kept) |
| 7 | Sun and shadow tool | done (SunPosition, TOOLS group) |
| 8 | Analyst tools (range rings, bearings, areas; saved privately) | done: MEASURE (AnalystGeometry); keeping shapes = existing private map notes |
| 9 | Briefing export (PDF) | |
| 10 | Multi-signal area rules | |

## Progress

- 13-11-26: Phase 1 done (above). Phase 2 started.

## Phase 1b: SentinelGPT Mac first, AI droplet second (13-11-26, Grant)

Cause of the 30-minute queue: SentinelGPT on the droplet was gemma4 (6.6 GB) under a 6 GB cap with
one model loaded; embeddings and generation swapped models 126 times in 45 min; one app-wide queue.

Built (code, tests in OllamaFailoverTests; not yet live):
- OllamaFailover.cs: OllamaFailoverMonitor (5 s probes, down after 2 misses, up after 3 answers,
  request connection failure marks down at once, PrimaryLostToken cancels in-flight Mac work) and
  FailoverOllamaService (Mac first; rerun on droplet; streams fall back only before the first word;
  embeddings on the droplet, Mac only with the identical digest; pull/delete manage the droplet).
- Separate queue per machine (OllamaWorkloadScheduler ambient priority now static; backup queue
  signals interactive arrival to the main one).
- ServerAi:PrimaryOllamaUrl (compose SERVER_AI_PRIMARY_URL). Empty = droplet alone, as today.
- Status strip: "SentinelGPT idle · Mac" / "· backup" (warning tone on backup).

Still to do (in order):
1. Tailscale on the Mac (app; Grant signs in) and the web droplet (install script; Grant opens the
   login link). Mac: `tailscale serve --bg --tcp 11434 tcp://127.0.0.1:11434` (Ollama stays on localhost).
   Tailscale ACL: only the web droplet may reach the Mac on 11434.
2. Mac stays reachable: `sudo pmset -c sleep 0 disksleep 0 standby 0 autopoweroff 0; sudo pmset -a autorestart 1`
   (lid closed sleeps without an external display); Ollama and Tailscale open at login.
3. Droplet: sentinelgpt rebuilt on llama3.2 (same SYSTEM, num_ctx 8192), OLLAMA_MAX_LOADED_MODELS=2,
   OLLAMA_KEEP_ALIVE=-1 (fits the 6 GB cap with embeddinggemma).
4. Web .env: SERVER_AI_PRIMARY_URL=http://<mac-tailnet-ip>:11434; live failover test (quit Ollama on
   the Mac -> backup within ~10 s; reopen -> Mac within ~15 s).
5. Then: Overwatch ten features (night lights code is in the tree, untested live).

Decisions (Grant, 13-11-26, as recommended):
- Docker socket: kept as is (Admins keep every Docker Management action). Revisit with a socket
  proxy if remove/recreate/exec stop being used.
- codex-audit: kept until the Mac-first setup is finished and tested; THEN lock it
  (`sudo passwd -l codex-audit`, remove its authorized key). Do this as the last step of Phase 1b.

## Phase 1b LIVE (13-11-26)

- Tailscale: Mac `dagda` (Tailscale app, `tailscale serve --bg --tcp 11434 tcp://127.0.0.1:11434`,
  Ollama "Expose to network" OFF, so Ollama is localhost-only) and web droplet `gws-web`
  (`tailscale up --accept-dns=false --shields-up`). Web .env: SERVER_AI_PRIMARY_URL (Mac tailnet IP).
- Live test: forward off -> "Mac stopped answering" after 6 s; forward on -> "Mac is back" after 18 s.
- codex-audit LOCKED (passwd -l, nologin, sudoers file and key removed; backup in
  /root/gws-ai-backups/codex-audit-<time>). Grant's root key login checked first. Assistants now
  reach the AI droplet as root with Grant's Mac key.
- Left for Grant: Tailscale access rule (only gws-web -> dagda:11434), Mac power settings (pmset).
- 13-11-26: Tailscale access rule saved by Grant (gws-web -> dagda:11434 only; other Mac ports checked blocked). Mac pmset set. Overwatch 4, 6, 7 built.
