# SentinelGPT as an agent in the app (30-10-26)

Grant: "I am basically wanting SentinelGPT to be like Codex/Claude and can manipulate things within
the app." He confirmed this restatement:
- You describe the outcome and SentinelGPT carries out the steps through the app's own features.
- It works across the app, checks its work, and reports what it did with links.
- It remembers what it made in the chat, so "add one more task to that list" works.
- Changes are safe and can be undone.
- It's limited to what the asking user may do, on the web and in the Mac app.

Grant took the defaults I proposed (30-10-26):
1. **Approval.** Routine creates and edits happen at once with Undo. Publishing, deleting, sending, and
   anything that leaves the app wait for a Confirm card.
2. **Model.** The thinking runs on the Mac's local model; the Mac's gemma4-based sentinelgpt got 7/7 in
   the 2026-09-04 competence pass, the droplet's llama3.2 2/7. The changes always run on the server as
   the signed-in user. With no local model, the server's llama3.2 runs a short "simple mode" (few
   steps), so the droplet does no more than one chat costs today.
3. **Order.** Sentinel pages and to-do lists, boards, site-page drafts first; then CRM and automations;
   then the rest.

Grant, mid-build (30-10-26): "I want to ask SentinelGPT to do something within the app, and I want
SentinelGPT to research and then do." So research tools joined Phase A, and the prompt's first rule
became "research first, then do".

**Change after the live check (30-10-26).** On the droplet's llama3.2 in simple mode, the agent:
- claimed a to-do list it never created
- wrote a site page from nothing instead of the notes
- echoed its own prompt back on "Publish it"

Local sentinelgpt (gemma4-based) did all three right, in 3-12 s each. So the server never runs the
agent. With no local model, the panel uses the server's existing answer-only path, labelled
"answers only", with a one-time note pointing to the Mac app. Phase B's relay is how a browser
elsewhere will get the agent.

## Shape
- **Tools**: one registry, `ISentinelAgentTool`. Each tool has a name, a description, a JSON schema,
  a risk (Read / Change / Confirm), the roles allowed to use it, and a run method. A change tool also
  has an undo method.
  - Tools wrap the existing services (`IWikiService`, `IKanbanBoardService`, `ICmsBuilderService`, ...),
    so every save rule and revision still applies.
  - Role gates follow the pages: Sentinel = ContentAccess, site pages = Admin/Contributor,
    boards = Admin.
- **Runner**: `SentinelAgentRunner`.
  - Each turn it builds the prompt from:
    - the rules
    - today's date
    - the user's role
    - what's on screen
    - the chat's last exchanges
    - the working set (what this chat made or changed, with ids and links)
  - It loops model -> tools -> model until there's an answer, up to 8 rounds locally and 4 in simple mode.
  - A Confirm tool stops the turn with a card. Approving it runs that one action.
- **Action log**: `SentinelAgentAction`, one row per change or proposal. It holds the summary, the
  object, the link, the undo payload, and a status: Done / AwaitingConfirm / Declined / Undone /
  Failed. It is the source of the working set and of Undo. Turns are kept as `SentinelAiRun`
  (Action = "agent").
- **Thinker**: either `IOllamaService.ChatAsync` on the server, or the local Ollama through
  `BrowserLocalOllamaService.ChatAsync`. The local path goes through local-ollama.js `/api/chat`, and the
  Mac app's LocalOllamaBridge now also allows POST /api/chat.

## Phase A: core + Sentinel, boards, site pages, in the side panel (web + the Mac app's pages)
- [x] `SentinelAgentAction` entity, DbSet, migration (20261030131200_AddSentinelAgentActions).
- [x] Tool abstractions, toolbox (role filter), runner, prompt, working set, confirm, undo. Also:
  an `undo_change` tool, and a guard against the same change twice in one turn.
- [x] Sentinel tools, all with undo (`SentinelPageAgentTools`; create_todo_list was added for small models):
  - find_sentinel_pages, read_sentinel_page
  - create_sentinel_page (markdown, to-dos as `- [ ]`)
  - add_to_sentinel_page
  - set_todo_done
  - rename_sentinel_page
  - trash_sentinel_page (Confirm)
- [x] Board tools (`BoardAgentTools`, Admin):
  - list_boards, read_board
  - create_board
  - add_card
  - move_card
  - rename_card
  - delete_card (Confirm)
- [x] Site-page tools (`SitePageAgentTools`, Admin/Contributor; only edits Drafts, publishing needs Confirm):
  - find_site_pages
  - create_site_page_draft (sections of heading + text, optional section templates)
  - add_to_site_page_draft
  - publish_site_page (Confirm)
- [x] Research tools (`ResearchAgentTools`):
  - search_app (Admin)
  - search_world_news (Admin)
  - search_web and read_web_page (when OllamaWeb:ApiKey is set)
- [x] Local chat with tools:
  - local-ollama.js `startChat`, `BrowserLocalOllamaService.ChatAsync`, and the shared `OllamaChatPayload`
    (the server uses it too)
  - the bridge allows /api/chat
  - `SentinelAgentModels.LocalCandidates` picks the model; a "does not support tools" 400 moves on to the next one
  - The Mac app needs the update for the bridge change. Until then, inside the app the panel falls back to
    the server's answers.
- [x] Side panel:
  - agent turns show their steps, change cards (Open, Undo) and Confirm cards
  - the page refreshes when its own record changed
  - the server is answer-only (see above)
- [x] Tests (`SentinelAgentTests`, 11):
  - runner loop with a fake model, role filter, confirm/decline, undo
  - working set across turns ("add one more to that list" 3 turns later)
  - each tool against SQLite
- [x] Live check on local models (temporary test, removed):
  - Grant's list scenario, plus a research-then-do scenario (notes -> list -> site draft -> publish
    proposal), all right on sentinelgpt
  - llama3.2 failed (see above)
- [ ] User guide: SentinelGPT section in docs (on disk; docs/ is untracked).

## Phase B+: dependable before capable (Grant: "Do what you recommend", 30-10-26)
- [x] **"This" is the open record.** `AssistantPageContext.RecordId` is passed by Support tickets,
  CRM contacts, boards, Sentinel pages and the site-page editor. The prompt rule says "this/it/here"
  means that record. add_ticket_card makes a board card linked to the open ticket.
- [x] **Plan before big jobs.** `show_plan` card with Go/Cancel; Go sends `GoMessage`. The model
  ignored the prompt rule in the live check, so the runner enforces it:
  - At the 4th change without Go, it rolls back the turn's changes (Withdrawn events) and blocks
    further changes until the model plans the whole job.
  - Live result: plan, then Go, then each item made exactly once.
- [x] **Memory.** `SentinelAgentMemory` (migration AddSentinelAgentMemories) holds remember/forget
  tools with Undo. Memories go into every turn's prompt, each person's own. Live: "Where do we
  host?" was answered from memory in a new chat.
- [x] **What SentinelGPT did page.** `/admin/sentinelgpt/activity`:
  - Changes (Open/Undo/Confirm; Admins can see everyone's but act only on their own)
  - Memories (add/forget)
  - Linked from the panel header and Knowledge > SentinelGPT activity.
- [x] Tests: `SentinelAgentTests` now 16.

## Phase B: the Mac app's own SentinelGPT tab, and the web handing off to the Mac
- [ ] Native endpoints so the Mac tab's agent can list and run tools as the signed-in user:
  `/admin/api/native/agent/tools`, `/run`, `/confirm`, `/undo`.
- [ ] Mac SentinelGPT tab: the agent loop on local Ollama, with step, change and confirm cards.
- [ ] Relay: a browser with no local model queues the turn. The Mac app, when running, picks it up
  (like WorldNewsSummaryRelay), thinks locally, and runs tools through the server, while the browser
  streams the steps. After a short wait with no Mac, it falls back to simple mode.

## Phase C: CRM and automations
- [ ] CRM: find/create/update contacts, companies and deals, add notes, move deal stage (Admin).
- [ ] Automations: list, run, enable/disable, create from description (the existing proposal, now a
  Confirm card).

## Phase D: the rest
- [ ] Media, support tickets, time tracking, content calendar, blog post drafts, email (Confirm),
  settings (Confirm), Overwatch/World News reads.
