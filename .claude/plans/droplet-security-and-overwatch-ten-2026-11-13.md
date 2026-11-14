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
| 1 | Time slider for every layer (72 h replay + what changed) | done for layers with history: 72 h, all begun hazards, started/cleared since; live-only layers (aircraft, ships, outages) keep no history |
| 2 | Layer health panel | done: TOOLS > LAYER HEALTH (OverwatchLayerCatalog; problems since page open) |
| 3 | Satellite scenes on click (Earth Search STAC, before/after) | done via Planetary Computer (STAC + tiles, CORS); GIBS HLS too coarse in 3857 |
| 4 | Night lights (NASA Black Marble via GIBS) | done (web + Mac map) |
| 5 | Chokepoints and ports (IMF PortWatch) | done (ChokepointService, SEA group, Mac map) |
| 6 | Flight anomalies (emergency squawks, holding, diversions) | squawks done; holding/diversions need track history (not kept) |
| 7 | Sun and shadow tool | done (SunPosition, TOOLS group) |
| 8 | Analyst tools (range rings, bearings, areas; saved privately) | done: MEASURE (AnalystGeometry); keeping shapes = existing private map notes |
| 9 | Briefing export (PDF) | done: TOOLS > BRIEFING PDF (OverwatchViewBriefingPdf) |
| 10 | Multi-signal area rules | done: watched areas SIGNALS TOGETHER (WatchConvergence, migration) |

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
- 13-11-26: ALL TEN OVERWATCH FEATURES DONE (each pushed after the release gate).
- 13-11-26 follow-ups: #10 layer health shows each layer's last load time, slowest, failures and data
  time (OverwatchLayerCatalog.LayerLoads); #6 live-layer history (live-history.db, LiveTrackHistoryStore,
  aircraft/ships every 5 min, outages every 15, 72 h) replayed by the time machine; the Mac app's native
  map now draws the time machine (it drew nothing: one object holding lists); #4 convergence computed
  worldwide every 5 min while in use (ConvergenceRefreshService), wide views filter it.
  - Layer check of every toggle (web + Mac native path) started with a Playwright script and was stopped
    when Grant moved the priority to World News search: NOT DONE. Known Mac gap from reading the bridge:
    power outage polygons ("rings") aren't drawn natively (points are).
  - Not done: #3 file split, #5 Mac batching, #9 situation room.
- 13-11-26 MAC MAP CHECK (Grant: every Layers toggle must render on the Mac's MapKit map):
  - Method: Playwright drives every toggle with the Mac adapter on (macApp stand-in), saves each
    page->Mac message, and runs it through NativeShapes (the bridge's reader, now shared in
    Application/Overwatch so it's tested: NativeShapesTests, 26 with the contract tests).
  - Found and fixed (drew nothing on the Mac): ALERTS (rings), CITY TRAFFIC (multi-part paths + live
    speeds as a 2nd list), AURORA (grid), LIVE FLOW (signed-in tiles: SignedInTileOverlay with the web
    view's cookies), SHADE COUNTRIES (no coordinates: server now sends the country centre), time
    machine (one object of lists). Partly drawn, fixed: POWER OUTAGES areas (flat rings), SATELLITES
    orbits (lon,lat,height; split at the date line), CONVERGENCE squares.
  - Needs a Mac app update (installer / Check for Updates) as well as the server deploy.
  - Verified with real payloads through NativeShapes: every toggle with data draws (cameras 1,500;
    incidents; city traffic 4,000 roads; conflict 905; hazards 295; fires 1,500; power outages 29 + 54
    areas; convergence 18 squares; aurora 103 areas; satellites + ISS orbit; road temps 36 and signs 41
    in Montana; ships off Finland; alerts 12 / 483 zone areas). Shapes grouped into one multi-polygon
    and one multi-line overlay per layer (MapKit speed).
  - ALERTS drew nothing on the web or the Mac: NWS issues almost all alerts by zone with no outline
    (301 active, 0 with geometry). NwsAlertsService now fetches affected zones' outlines (cached 7 days,
    150 new zones a refresh, thinned to 120 corners).
  - Weather panel never showed in the Mac app (setWeatherSnapshot only went to the native map): the
    adapter now runs it on the page too (MacApp_ShouldShowTheWeatherPanel browser test).
  - Not checked with live data: SHADE COUNTRIES (local copy has no placed stories) and LIVE FLOW (no
    TomTom key locally); both covered by NativeShapesTests and the Mac build.
