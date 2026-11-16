# AI workload routing and adaptive SentinelGPT jobs

## Goal

- Full article creation and revisions prefer the Mac's larger Ollama models.
- Simple SentinelGPT answers prefer the AI droplet; tool-based and planned work prefers the Mac.
- Either compute source falls back to the other when available.
- Replace the perceived eight-step completion ceiling with progress-based continuation and saved resumable work.

## Progress

- [x] Trace the Content Studio server-only warning and current failover service.
- [x] Add request-scoped Mac/droplet routing to the shared Ollama abstraction.
- [x] Route article generation and revision work explicitly to the Mac.
- [x] Route initial simple SentinelGPT answers to the droplet and subsequent tool/job work to the Mac.
- [x] Remove the ordinary round-count completion limit and use progress-based continuation.
- [x] Complete focused tests and the full release gate (`PARTIAL` only because deployed targets were not supplied).

## Guardrails

Adaptive jobs stop after repeated calls or two consecutive legs without completing a step, after
24 legs, or after 30 minutes. A high 64-round emergency circuit breaker remains for pathological
model output, but reaching it moves work into the saved job pathway rather than declaring the task
finished. Explicit small round counts exist only for deterministic tests and diagnostics. The saved
plan reports exactly what remains and can resume with "carry on".
