# AI droplet: fast and available (19-11-26)

Grant: "build a way to work the Ollama droplet to do better and be available", after the
`fed9fac1` deploy and its rollback both failed `/health/ready` (HTTP 503).

## Found (read-only, gws-ai over the web droplet)

- 4 vCPU / 8 GB, Ollama under systemd (`/etc/systemd/system/ollama.service.d/gws.conf`), SearXNG in
  Docker. Load ~6. One `llama3.2` runner 20 h old at 0.10 tokens/s (normal 10-20), 75% RAM;
  `/api/version` took up to 2m20s; the web app's 5 s readiness check failed.
- Cause: `MemoryHigh=5G` < llama3.2's real footprint (~6 GB: 619 MB buffer + 1.3 GB repacked copy
  + mapped file + 8K context). `memory.events high` = 1,468,751; memory pressure full ~21%;
  4 GB swap full. CPU pressure low.
- `gemma4` (6.3 GB) installed but not loaded in 3 days; `sentinelgpt` missing (the agent's family
  fallback could pick gemma4). All work ran on llama3.2.
- The app's `OllamaWatchdogBackgroundService` only restarts a local container, so nothing
  restarted the remote Ollama.

## Built

- `scripts/setup-ai-droplet.sh` (idempotent): no MemoryHigh, MemoryMax=7G, MemorySwapMax=512M,
  swappiness 10, Restart=always; models llama3.2 + embeddinggemma + sentinelgpt (repo profile on
  llama3.2, num_ctx 8192), `--prune` the rest; `gws-ollama-watchdog` timer (version check every
  minute, restart on 3 misses or 5 min of memory stalls >= 10%, 10 min cooldown, journal tag).
- `OllamaHealthCheck`: Degraded instead of Unhealthy, so readiness (and deploys) don't depend on the
  AI droplet (`OllamaHealthCheckTests`).
- Platform Operations guide section.

## Progress

- [x] Diagnosis, script, health check change, tests, guide.
- [x] Applied on gws-ai 19-11-26 14:13 UTC with Grant's explicit OK (`--prune`; old drop-in kept as
      `gws.conf.bak-<stamp>`). Removed `gemma4` and `sentinelgpt-gemma4`; `sentinelgpt` is now
      llama3.2-based (2.0 GB). Result: 4.2 tokens/s on live requests (was 0.10), `memory.events
      high` +0 over 20 s (was 1.47 M), memory pressure ~0%, swap 11 MB (was 4,095), 3.4 GB free;
      production `/health/ready` 200 again.
- [ ] Next deploy (Grant's push) goes through; watch `journalctl -t gws-ollama-watchdog` for restarts.
