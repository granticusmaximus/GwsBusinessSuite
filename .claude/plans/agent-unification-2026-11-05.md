# One rulebook for every assistant (05-11-26)

Grant: "Make this correct and unified", after an audit of gaps left by Claude, Codex and Copilot.

## What the audit found

- **Rules reached Claude only.** CLAUDE.md held them. The repo had no AGENTS.md, and Codex's global
  one was empty. `.github/copilot-instructions.md` said "run dotnet build and dotnet test", not
  verify-release, and had no push rule.
- **02-11-26: two failed production deploys.**
  - f5bb35f removed `IKanbanBoardService` members that code still called.
  - Copilot PR #49 re-added `KanbanBoardAccess` a second time. Copilot had checked it in a
    worktree created on 27-05-26 and reused into November, 816 commits behind `main`. The same
    stale base led it to rebuild Mail (PR #51, never merged).
- **The same Gmail SMTP bug went to three assistants.** The cause was DigitalOcean's SMTP block,
  fixed in eef0467.
- **Two big changes had no plan file:**
  - **Moving AI to the Mac** (2ca459c, 31-10-26, Codex): production `ServerAi__Mode: None`, with
    the Mac app's relays doing the model work.
  - **The neumorphic redesign** (c6e13d4, 7837b7a, 03-04-11-26, Codex): Grant approved the
    prototype, then asked for it "site wide. To every page in the admin and to the front end as
    well".
- **The guides had fallen behind.** Sentinel and Content Creation still described server AI as
  llama3.2 / Light mode. SentinelGPT's plan limit had changed from 3 to 25 routine changes. New
  agent tools, permissions, presence and live updates were undocumented.
- **Duplicates:**
  - KanbanPresence was a copy of RecordPresenceTracker, and RecordPresenceTracker had no tests.
  - Content Studio's two busy overlays had a white Bootstrap-spinner panel styled inline twice,
    ignoring the boot screen and the neumorphic surfaces.

## Progress

- [x] `AGENTS.md`: the single rulebook for shipping, starting from current code, architecture,
  traps, environment, and docs/tracking.
- [x] `.github/copilot-instructions.md`: now points to AGENTS.md and repeats the five rules that
  broke production.
- [ ] `CLAUDE.md` should import AGENTS.md (`@AGENTS.md`). The classifier blocked Claude from
  editing its own instructions, so Grant makes this change.
- [x] `KanbanPresence` is now a board-shaped view of `RecordPresenceTracker`, which gained an
  optional focus (the open card). There's one tracker, and tests for sharing it.
- [x] `BusyOverlay`: Content Studio's busy state uses the boot screen, styled once in app.css.
- [x] Guides updated: Sentinel, Content Creation, Platform Operations (new "Server AI and the Mac
  app" section, SMTP block), Support, CRM.
- [ ] GitHub: delete Copilot's `agents/vscode-resume-prompt-guide` branch, which closes PR #51.

Still unverified (needs a real device or account, so not done here):
- the SentinelGPT agent live
- Mail against a real mailbox
- the 13c353d WebView caching fix in the Mac app
- the Overwatch radar click-through
