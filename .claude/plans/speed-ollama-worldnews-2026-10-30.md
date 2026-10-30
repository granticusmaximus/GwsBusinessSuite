# Speed: Ollama and World News (30-10-26)

Grant: the app was very slow, the Mac app sat on "Reconnecting", and Ollama needed restarting by
hand every time he opened the app.

## Found on the droplet (read-only SSH, 30-10-26)
- 4 vCPUs, 7.9 GB RAM, no swap. Load 7.35. Ollama at 371% CPU and 4 of its 5 GB; the web app
  at 3.5%. Two llama-server runners at once: embeddinggemma (160 CPU-minutes in 4 hours) and
  llama3.2 (3.3 GB) generating.
- No CPU limit on the Ollama container. World News embedded up to 1,500 headlines every 15 minutes
  on its own Ollama client, outside the app's one-at-a-time scheduler, oldest first (so always
  catching up on stale news), while llama3.2 wrote summaries.
- The app's Ollama HTTP client times out after 2 hours, and every AI call shares one slot.
- Removed at Grant's request: podcast-directory, heimdall, code-server, GWS Connect (repo-client-1,
  repo-server-1). Data left on disk. connect.gwsapp.net still has a route in
  /etc/cloudflared/config.yml (Grant to decide; removing it restarts the tunnel).

## Done (30-10-26)
- [x] docker-compose: Ollama `cpus: 3`, `cpu_shares: 256` (the app always has a core and wins contention).
- [x] World News embeds newest first and only within the 48-hour event window
  (`RecentArticlesToEmbed`), one batch at a time through the scheduler at background priority
  (`WorldNewsOptions.EmbedSlot`).
- [x] Server summaries: 1 per read (was 3), background priority, and a running one stops when
  interactive work queues (`OllamaWorkloadScheduler.InteractiveArrivedToken`); summarized later.
- [x] `OllamaWatchdogBackgroundService`: restarts the Ollama container after 4 unanswered
  `/api/version` checks (30 s apart) or when one job holds the slot 10 minutes past the configured
  AI timeout; 15-minute cooldown; logged and in Docker Health's action log.
- [x] Overwatch World News: `WorldNewsMapCache` (shared, cleared on each read and correction) for
  the ranked situations, placements, growth and per-situation details; the reads run off the
  circuit (`Task.Run`) with a stale-result guard; shading recomputed only on toggle/kind/window,
  and the country shapes are kept and recoloured rather than rebuilt; `Situation(id)` is a direct query.

- [x] Deployed (f47c5b3). Droplet after: Ollama held at ~300% (its cap), the app 1-25%, load 9.6 -> 3.3.
- [x] The site's model override (`muse-glimmer:latest`) isn't on the droplet, so web SentinelGPT
  failed: `OllamaService` now falls back to the configured default model when the chosen one isn't
  installed (logged once per model), and the warmup warms that default instead.
- [x] 511 cameras: one in-flight download per site, the old list keeps serving while a fresh one
  downloads (fresh 30 min, kept 6 h), pages after the first fetched 4 at a time.
- [x] Cesium render-on-demand (requestRenderMode) for the web and the Mac app's WebView: frames on
  entity/primitive/layer changes and every exported call, a 1 s backstop frame, ~20 fps only while a
  SURGING marker pulses (browser test counts drawn frames).

## Next
- [ ] Timing logs for globe moves and Ollama queue waits.
- [ ] After deploy: re-check the droplet (docker stats during a read) to confirm the app keeps a core.
- [ ] Boards K9g (sharing) is paused, nothing written.
