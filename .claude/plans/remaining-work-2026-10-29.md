# Remaining work (Grant's answers, 29-10-26)

Grant's decisions on the open items, and the build that follows from them. One unit each; the
Boards features are one phase, as Grant asked.

## 1. 511 cameras, key-less ("go keyless when possible")
The 511 platform's public map serves its camera list key-less at
`/List/GetData/Cameras?query={...}` (header X-Requested-With) and stills at `/map/Cctv/{imageId}`
(plain JPEG, no key). Probed live 29-10-26: 17 sites answer. One provider, one registration per
site: Alaska, Nova Scotia, New Brunswick, Manitoba, Saskatchewan, Yukon, PEI, Arizona, Wisconsin,
Nevada, Utah, Louisiana, Connecticut, Pennsylvania, Ontario, Alberta (Newfoundland and North
Carolina answer 500; New York returns another format; New Jersey 403). The developer-key API
(option A) isn't used, so the key Grant added isn't needed.
- [x] `Platform511CameraProvider` + registrations + coverage-list entries + guide.

## 2. Accurate World News summaries without growing the droplet
Measured 29-10-26 on real stories: llama3.2 (the droplet's model) won't follow a citation format,
so a rule-based check can't make its summaries trustworthy; gemma4 (doesn't fit the droplet's 5 GB
Ollama cap) was clean on all three. Route: Grant's Mac writes them, as article generation already
works.
- [x] Built on the Mac app's signed-in session instead of a developer key (no setup):
  `/admin/api/native/world-news/summary-jobs` and `/summaries` (AdminOnly + X-GWS-Native).
- [x] Mac app `WorldNewsSummaryRelay`: every 15 minutes while open, gemma4 (or gemma3:27b,
  qwen3:32b, gemma3:12b, qwen3:14b) answers up to 5 jobs and sends them back.
- [x] The server's small model stands aside while the Mac asked within 2 hours, and takes over as
  a labelled fallback when it hasn't. Needs the Mac app rebuilt/updated to take effect.

## 3. Claims: "Coverage disagrees"
The 28-10 gate failed because a model misattributed figures. Built without a model instead:
numbers are read from each source's own headline and description with their counted noun (dead,
injured, arrested, missing, evacuated, age, ...) in English plus the main European languages.
Same measure, different values, from different outlets = a disagreement listing each value with
its sources; one value from 2+ outlets = consensus. "At least 40" and "64" don't conflict; when
the lower numbers are all older the panel says the figure may have risen.
- [x] `CoverageClaims` in WorldNewsKit, "Coverage figures" on the event view, tests. (Events only:
  a situation spans days, so its tolls rising would read as disputes.)

## 4. Sentinel Timeline drag-to-resize
Already shipped 28-10-26 (57f596b8): a second Date property is the end, edge handles resize.
Nothing to build.

## 5. Boards K9c-K9g, one phase
As specified in kanban-boards-2026-10.md: Waiting on, card face fields, epics, email cards onto
a board, share a board (then board notifications reach shared users).
- [x] K9c and [x] K9d were committed by another agent (7df5a5c, 9282aca) while this was planned;
  Claude stopped before writing K9 code to avoid a collision. K9e-K9g: check git before building.
- [x] Added at Grant's request (29-10-26): column management on the board (rename, colour, group,
  move, add, delete with card destination) - KanbanColumnEditor, KanbanAddColumn, tests.

## Done when
Tests for each, guides updated, verify-release green, live checks where a browser can reach it.
