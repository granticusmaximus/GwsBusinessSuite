# Handoff: Sentinel inline writing assistant

## Do this first

**The release gate has not been run on this work.** `CLAUDE.md` requires it before the turn
ends, and I ran out of budget before it completed. Nothing here is verified beyond the unit
tests.

```
./scripts/verify-release.sh
```

If it fails, fix forward — do not revert. There are no migrations and no schema changes in this
work, so a failure is a compile or test problem, not a data problem.

## Repo state

HEAD is `35c9c29`. Everything below is **uncommitted** working-tree state. Do not commit or push
without the user asking — they commit manually, sometimes outside the session.

| File | State |
|---|---|
| `src/GwsBusinessSuite.Application/Wiki/SentinelWritingAssistant.cs` | new — action catalog, prompts, response cleaning |
| `src/GwsBusinessSuite.Application/Wiki/SentinelWritingAssistantService.cs` | new — Ollama call, model resolution, timeout |
| `src/GwsBusinessSuite.Infrastructure/DependencyInjection.cs` | +1 line, registration at line ~301 |
| `src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Wiki.razor` | +1 inject, +2 `[JSInvokable]` (`GetWritingActions`, `RunWritingAction`) |
| `src/GwsBusinessSuite.Web/wwwroot/js/wiki-block-editor.js` | AI button + menu in `showInlineToolbar`, `state.writingActions` field |
| `src/GwsBusinessSuite.Web/wwwroot/app.css` | `.wiki-ai-menu*` styles |
| `tests/GwsBusinessSuite.Tests/SentinelWritingAssistantTests.cs` | new — **21 tests, all passing** |
| `docs/SENTINEL_USER_GUIDE.md` | new "Writing assistant" section at line ~144 |

## What this feature is

An **AI** button at the left of the Sentinel editor's inline selection toolbar. Select text →
pick one of 7 actions (improve / shorten / lengthen / fix / simplify / professional / continue)
→ the result replaces the selection in place. `continue` appends instead of replacing.

Design decisions worth preserving if you refactor:

- **The action set is closed.** The browser sends an action *key*, never a prompt. Page content
  cannot reach the system prompt. This matches the user's stated preference for closed grammars
  over free-form runtimes (same call they made on the n8n expression engine).
- **The catalog is server-owned.** JS fetches it via `GetWritingActions` and caches per editor
  instance, so adding an action in `SentinelWritingActions.All` is enough to make it appear.
  Don't add a parallel copy in JS.
- **Model output is inserted as a text node, never as markup.** That is the entire XSS story.
- **`CleanResponse` strip order matters** and there is a test pinning it: `<think>` blocks come
  off *first*, because a reasoning block can itself contain fences and "Here is..." lines.
- **A runaway response returns empty rather than truncating.** Truncating would paste a fragment
  the user wouldn't notice. 3× the original length, 6× for growth actions.
- Timeout is **2 minutes**, deliberately generous — a cold local model is slow on first call, and
  an aggressive ceiling is what caused the GovIntel Ollama failure the user already debugged once.

## Verification still owed

1. `./scripts/verify-release.sh` — the blocking item.
2. **Nobody has clicked this button.** The C# is unit-tested; the JS path is not covered by any
   Playwright test. Worth a manual pass in the running app: select a sentence, open **AI**, run
   *Improve writing*, confirm the text is replaced and the page then saves.
3. The menu opens *below* a toolbar that floats *above* the selection, matching the existing
   colour menu. Check it doesn't clip at the top of the viewport.

## Do NOT build these — they already exist

I published a comparison artifact for the user
(<https://claude.ai/code/artifact/097215b7-b4cd-4449-91a0-cdcbba97502f>) listing 17 parity gaps.
**Six of its items are wrong** — I inferred "empty table = missing feature" and the features are
in fact built. Verified present in source:

| Artifact item | Reality |
|---|---|
| 05 Seed template gallery | `SentinelStarterTemplates.cs`, 3 templates, wired into "+ New page" |
| 06 Comment button | Already in the selection toolbar → `OpenSelectionDiscussion` |
| 08 Drag handle | `.wiki-block-handle` exists in JS **and** `app.css` |
| 09 Multi-block selection | `state.blockSelection`, cross-block selection implemented |
| 10 Notifications / saved searches | `SentinelCollaborationService`; saved searches in `Wiki.razor` |
| 12 Row revisions | `WikiDatabaseRowRevisions` fully read/written/restored |

The tables are empty because this is a **single-user deployment that hasn't used them**, not
because anything is missing. Verify before building anything from that list — the same mistake
bit me three times in one session, and the repo memory
(`feedback_multi_agent_handoff_verify_current_state`) already warns about exactly this.

Also partially wrong: item 07 said "Markdown-only export" — `WikiCsvExporter.cs` exists too, so
it is Markdown **and** CSV. PDF/DOCX/HTML are genuinely missing.

## Genuinely missing, verified

In the user's approved order. Inline AI (above) was the first of these and is now built.

1. **CRDT co-editing** — presence and remote cursors exist; `ContentVersion` gives a *conflict*,
   not a merge. No CRDT/OT library anywhere in the solution.
2. **Offline** — no service worker in `wwwroot`. `state.isOffline` exists in the editor but only
   reflects `navigator.onLine`.
3. **Mobile** — 10 media queries for the whole admin app.
4. **Export to PDF / DOCX / HTML.**
5. **Row virtualisation** — `MaxRows = 10_000` loaded at once, no `Virtualize`. Notion degrades
   past ~5k; Sentinel is at 2,798 today.
6. **Public API + MCP server** — no `/api/wiki` endpoints, no MCP package referenced.
7. **Wiki automation nodes** — the 35-node registry reaches Sentinel *rows*
   (`database.addRow`, `wikiDatabaseId`) but not *pages*. The user's next queued item is
   `wiki.pageChangedTrigger` / `wiki.createPage` / `wiki.appendBlock` / `wiki.findPages`, built
   the way `civic.extractEvents` was.

## House rules that will bite you

- **No inline `<script>` anywhere.** CSP is `script-src 'self' https://cdn.jsdelivr.net` with no
  `unsafe-inline`; inline scripts fail silently in every deployed environment and Playwright
  route-stubs hide it because they serve no CSP header.
- **Zero warnings** is part of the user's definition of done.
- Update the relevant `docs/*_USER_GUIDE.md` as part of the change, not as follow-up.
- The user runs Codex and Claude in tandem on this repo — check `git status` for collisions
  before editing.
