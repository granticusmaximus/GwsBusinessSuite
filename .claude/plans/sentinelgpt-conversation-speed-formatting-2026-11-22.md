# SentinelGPT conversation, speed and answer formatting (22-11-26)

Grant asked for another pass on SentinelGPT focused on three outcomes:

1. Chat should feel natural rather than procedural or robotic.
2. Answers should begin and finish faster.
3. Answers should be formatted for the kind of response instead of using one shape for everything.

This file is the shared handoff for Codex, Claude and Copilot. Read it together with
`.claude/plans/sentinelgpt-agent-2026-10-30.md` and `AGENTS.md`. Preserve the existing permission,
Confirm, Undo, durable-job, Mac-first routing and verified-completion safeguards.

## Current implementation findings

- `SentinelAgentRunner.SystemPromptAsync` is dominated by operational rules and requires final
  answers to be one to three short sentences. This keeps answers compact but makes conversation
  clipped and repetitive.
- Agent history contains the latest six persisted turns, with each side capped at 1,500
  characters. There is no durable summary of earlier conversational decisions or tone.
- Tool-calling agent rounds use non-streaming `IOllamaService.ChatAsync`. The server answer-only
  path streams through `SentinelGptGenerationCoordinator`, but an agent's final prose appears only
  after its last full chat round completes.
- Opening the panel already calls `SentinelModelLoader.SentinelOpened`, so model warm-up begins
  while the person types.
- `LocalCandidatesAsync` probes the available model connection at turn time. The panel polls
  server generation every 150 ms and relayed Mac turns every 500 ms.
- Context preparation loads history, previous actions and memories and constructs tool definitions
  for every turn. Relevant tool areas are filtered, but the independent context reads are not
  gathered concurrently.
- `AssistantMarkdown` safely supports paragraphs, lists, emphasis, links, pipe tables and code.
  The panel styles these basics but has no response-type presentation, source treatment or
  progressive disclosure for lengthy answers.
- The 22-11 trustworthy-completion unit prevents unsupported completion claims. Do not weaken or
  bypass `SentinelAgentCompletionVerifier`.

## Unit 1: natural conversation and adaptive response shape

### Implementation

- Separate conversational behavior from operational tool rules in the agent system prompt.
- Default to a direct, natural answer; mirror the person's level of detail and terminology without
  imitating typos or becoming overly familiar.
- Avoid stock openings, restating the request, repeated apologies and narration of obvious tool
  use. Use contractions and ordinary language where appropriate.
- Ask one focused clarifying question only when missing information materially changes the result;
  otherwise make a safe, stated assumption and proceed.
- Refer naturally to the recent conversation and working set (for example, "the same board as
  before") without exposing ids.
- Use an adaptive formatting contract:
  - simple answer: direct paragraph;
  - explanation: answer first, then concise supporting detail;
  - completed action: natural confirmation, leaving Open/Undo to the action card;
  - comparison: a compact table only when it improves comprehension;
  - procedure: numbered steps;
  - uncertainty: distinguish confirmed facts, inference and unknowns;
  - research: source links beside the claims they support;
  - failure: what failed, what remained unchanged and the next useful action.
- Add regression tests that inspect the system prompt and ensure the completion verifier still
  rejects unsupported action claims.
- Update `docs/SENTINELGPT_AGENT_SETUP.md`.

### Acceptance

- Prompt tests prove natural-style and adaptive-format instructions are present.
- Existing Sentinel agent, permission, Confirm, Undo, relay and completion tests remain green.
- `./scripts/verify-release.sh` passes its local gates.

## Unit 2: faster first response and lower turn latency

### Implementation

- Add structured latency measurements for context preparation, queue delay, model readiness, time
  to first token, tool execution, final generation and failover. Surface aggregate evidence in the
  existing AI health/logging experience before tuning blindly.
- Cache a successful local/server model probe for a short bounded period and refresh it in the
  background; invalidate immediately on a connection failure or failover state change.
- Gather independent context reads concurrently where their EF/service lifetimes make that safe;
  use separate scopes/contexts when required rather than sharing one `DbContext` concurrently.
- Reduce prompt payload: relevant memories only, compact older history, relevant page fields and
  only the tool schemas needed for the detected areas. Preserve `discover_capabilities` and
  `use_tools` for expansion.
- Add a fast conversational path for requests that clearly require no application tools, while
  retaining access boundaries and Mac-first routing.
- Stream the final prose round after all tool calls finish. Tool-selection rounds remain
  non-streaming so structured calls stay atomic. Preserve the rule that a stream cannot silently
  move machines after visible text has begun.
- Measure before/after median time to first activity, first visible answer text and completion in a
  deterministic local test harness plus live evidence when available.

### Acceptance

- No extra model call is added merely to classify a turn.
- Tool requests still execute exactly once and completion verification still runs.
- A normal no-tool question shows useful text before the full answer completes.
- Mac failure still moves an unstarted answer to the droplet; a partially visible answer follows
  the existing explicit interruption behavior.
- Focused latency tests and `./scripts/verify-release.sh` pass.

## Unit 3: answer presentation and responsive polish

### Implementation

- Keep the prose and action receipt separate: answer text explains the outcome; existing action
  cards own Open, Undo and Confirm.
- Improve Markdown typography for headings, blockquotes, inline code, code blocks, lists, links and
  tables using existing GWS neumorphic tokens and `::deep` scoped CSS.
- Add compact source presentation when an answer includes supported links. Do not invent sources
  or collect unrelated links at the bottom.
- Collapse verbose tool steps behind an accessible "What I checked" disclosure after completion;
  keep the current step visible while work is running.
- Add sensible overflow behavior for code and tables without forcing the whole panel wider.
- Validate keyboard interaction, screen-reader status announcements, reduced motion, forced colors,
  narrow phone, tablet and desktop container sizes.
- Add/extend Playwright coverage for representative simple, procedural, research, action and error
  responses.

### Acceptance

- Answers remain readable without Markdown knowledge and raw HTML stays disabled.
- No horizontal page overflow at supported widths; wide code/tables scroll within their answer.
- Activity, sources, Confirm and Undo remain keyboard accessible.
- Visual evidence covers narrow, tablet and desktop layouts.
- `./scripts/verify-release.sh` passes its local gates.

## Progress

- [x] Repository-grounded audit and shared plan recorded.
- [x] Unit 1: natural conversation and adaptive response shape. Focused tests pass (51/51).
      `./scripts/verify-release.sh` is `PARTIAL`: all local checks passed on the final run; deployed
      and responsive targets were not supplied. The first full-suite run had one unrelated
      Overwatch idle-frame timing failure; its exact test passed immediately on rerun, followed by
      the clean full gate.
- [ ] Unit 2: faster first response and lower turn latency.
- [ ] Unit 3: answer presentation and responsive polish.
- [ ] Final cross-unit browser/responsive validation and commit-message recommendation.

## Handoff rules

- Finish and validate one unit before starting another; do not leave a partly applied cross-cutting
  rewrite.
- Before editing, inspect `git status`, the index, this plan, the current commit and
  `HEAD..origin/main`. Preserve another agent's or Grant's changes.
- Do not push, merge, deploy or publish without Grant's explicit permission in the current session.
- User-visible changes require the SentinelGPT user guide update even though `docs/` is ignored.
- The full local completion gate is `./scripts/verify-release.sh`; report `PARTIAL` accurately when
  deployed/responsive targets were not supplied.
