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
- [x] 9bd76edf deployed (14:38). Reads no longer fail, discovery runs on SearXNG (no 402s), but no
      read finished in 17 min: every background generation was cancelled at 34 s-2 min (Ollama
      logs 500) and the llama3.2 runner started 18 times in 40 min. `/api/ps` sampling showed
      sentinelgpt reloading from 16384 to 8192 context: callers send num_ctx 4096 (localization,
      SEO), 8192 (SentinelAI, summarizer), 16384 (World News English and analysis, chat payloads),
      and Ollama reloads the model on every change. Prompt reading was 9-20 tokens/s as a result.
- [x] `ContentStudio:ContextLength` (compose: `OLLAMA_CONTEXT_LENGTH`, default 8192) pins num_ctx on
      every OllamaService path (generate, stream, warm, chat via `OllamaChatPayload.Build(numCtx)`);
      matches the droplet's OLLAMA_CONTEXT_LENGTH and sentinelgpt num_ctx. Largest prompts seen
      ~2,400 tokens. Test: `EveryRequest_ShouldAskForThePinnedContext_SoOllamaNeverReloadsTheModel`.
- [ ] After deploy: runner starts stop (`journalctl -u ollama | grep -c "starting llama-server"`),
      World News reads finish, Today shows today's stories, future-dated reports gone.

## 19-11-26 afternoon: SentinelGPT slow in World News (Grant)

- Droplet CPU pressure 96%, embedding runner and chat runner running at once; 3.2 tokens/s writing,
  14-20 reading; 14 of 19 generations timed out in 30 min. No reloads (context pin held).
- Cause 1: production couldn't reach the Mac (8 s timeout). Tailscale was stopped on Dagda. Started it
  (Grant: "investigate and fix"); production reaches the Mac in 0.18 s; the Mac answered 6 jobs in
  7-13 s each.
- Cause 2: `FailoverOllamaService.PreferMac` (15-11) sent every request under 12,000 chars, 2,048
  output tokens, 16K context or 8 tools to the droplet first, falling back only on failure. Grant:
  the Mac does most of the work, the droplet is the fallback. Now Mac first unless a caller asks for
  PreferDroplet (`ShortRequests_ShouldGoToTheMacFirst_AndOnlyFallBackToTheDroplet`).
- Cause 3: a Mac that is down at start-up or stays down logged once. `OllamaFailoverMonitor` now
  repeats the warning hourly with what to check (`AMacThatStaysDown_ShouldBeReportedAgainEveryHour`).
- Note: `--prune` on 19-11 also removed the droplet's `sentinelgpt-gemma4` copy (6.3 GB, couldn't fit
  in RAM alongside the rest); the Mac has gemma4.
- [ ] After deploy: Mac handles summaries/hot takes/overviews; droplet generate failures stop.

## 19-11-26: Mac first, droplet only as backup (hardened)

- [x] `OllamaService` sends `think: false` on every generate. The Mac's gemma4 `sentinelgpt`
  reasoned before every answer ("Say OK." took 21 s), so jobs queued and timed out.
- [x] `OllamaComputeRouting` (the `PreferDroplet` hint) was removed along with
  `FailoverOllamaService`'s droplet-first paths. SentinelGPT's first chat round and Trend Research
  used to start on the droplet; every request but embeddings now starts on the Mac.
  `OllamaFailoverTests` covers every request kind, plus a guard that no droplet-first route exists.
- [x] Mac checked: Ollama launches itself (`com.ollama.ollama`), and Tailscale is connected with
  `serve` on 11434. On mains power the MacBook never sleeps (`sleep 0`, `womp 1`). On battery it
  sleeps after a minute idle, and the droplet covers those times, as designed.
- [x] AGENTS.md rule added.

## 22-11-26: stalled Mac generation handoff

- [x] A Mac whose health endpoint answers but whose model runner produces nothing now has a separate
      two-minute attempt deadline. The request then moves to the AI droplet while the caller's own
      cancellation budget remains alive; caller-requested cancellation still never falls back.
- [x] World News analysis has a six-minute overall budget so the droplet retains up to four minutes
      after a stalled Mac handoff. Its timeout now says both machines timed out rather than implying
      one unidentified SentinelGPT connection failed.
- [x] Focused failover tests pass (25/25). `./scripts/verify-release.sh` is `PARTIAL`: all local
      checks passed, including the Release build and full automated suite; deployed and responsive
      targets were not supplied.

## 22-11-26: prevent silent droplet-only startup

- [x] Centralized server AI (`SingleSource` with `Full` or `Light`) now requires a valid
      `ServerAi:PrimaryOllamaUrl`; startup fails clearly instead of silently registering the AI
      droplet as the only model runtime.
- [x] SentinelGPT's empty-state copy now states the actual policy: Mac models first, AI droplet
      only while the Mac is unreachable.
- [x] Focused tests pass (23/23). `./scripts/verify-release.sh` is `PARTIAL`: all local checks
      passed, including the Release build and full automated suite; deployed and responsive
      targets were not supplied.
