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

## Unit 2 - leftover work (05-11-26, Grant: "have no more left over work")

Production logs showed what moving AI to the Mac (31-10-26) left behind, plus failing sources.
- [x] **Shared work runs on an Admin's Mac.** `MacRelayOllamaService` replaces
  `DisabledOllamaService` in None mode. Inside `MacRelayScope.ForSharedWork()` it relays to the
  Admin Mac that asked most recently, through `SentinelAgentRelay`'s new low-priority lane, so
  the person's own steps go first. Everything else fails closed as before. This covers Government
  Intelligence overviews (~1,165 skips a day), Media Watch hot takes (~200) and Civic Watch
  summaries. It's quiet while no Mac is online, and the installed Mac app answers with no update.
- [x] **World News kinds:** the 45 example phrases ride the Mac's embed relay (`kind:<n>` jobs).
  They had been falling back to keywords, ~97 times a day.
- [x] **Refusing sites** (Eventbrite 405, gov.georgia.gov and senate.gov 403):
  `RefusedHostPauseHandler` leaves them alone for 24 h with one log line. It doesn't work around
  them.
- [x] **congress.gov `DEMO_KEY`** (429s): refresh every 6 h instead of hourly, and 3 hearings per
  chamber instead of 8.
- [x] **EF warnings:**
  - 39 single-row settings reads got `OrderBy(Id)`.
  - Single queries are now set explicitly, as before.
  - `AsSplitQuery()` on the three loads of every Sentinel database with rows.
- [x] **Dependencies** (Dependabot #47, #50): Fido2 4.3.2, HtmlSanitizer 10.0.1120 (parameterless
  constructor, unaffected by the options change), Ical.Net 5.2.4, Stripe.net 52.4.2 (same major).
- [x] Guides: Platform Operations and Intelligence. AGENTS.md: environment notes.

Needs Grant (not code):
- a free congress.gov key (`CongressApi__ApiKey`)
- connecting a Google account under Automation > Credentials, so the app's email works
- the DigitalOcean SMTP unblock, for iCloud
- the `@AGENTS.md` line in CLAUDE.md
- deleting the Copilot branch (both blocked for Claude)
- the deferred product decisions: 511 key, timeline drag-resize, World News claims, the
  Woffice-scale modules

Still unverified (needs a real device or account, so not done here):
- the SentinelGPT agent live
- Mail against a real mailbox
- the 13c353d WebView caching fix in the Mac app
- the Overwatch radar click-through
