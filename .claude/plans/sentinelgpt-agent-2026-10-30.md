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
- [x] User guide: SentinelGPT section in docs (on disk; docs/ is untracked).

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

## Phase B: the Mac app's own SentinelGPT tab, and the web handing off to the Mac (built 30-10-26)
The design changed from the first sketch. Instead of the Mac running the loop and calling tool
endpoints, the server keeps the one loop (the runner) and the Mac only answers its model calls. So
plans, memory, roll-back, Confirm and Undo are the same everywhere.
- [x] Relay (`SentinelAgentRelay` + `RelayedOllamaService`):
  - per-person queue of /api/chat payloads, handed only to a Mac signed in as that person
  - the Mac long-polls `GET /admin/api/native/agent/chat-jobs?models=` (25 s) and replies with
    `POST .../chat-jobs/{id}`
  - a 3-minute wait per call
  - `ModelsFor` = the Mac's tool-capable models while it's polling (seen within 60 s)
- [x] Mac app `SentinelAgentMacRelay`: started with the app; runs each payload on 127.0.0.1:11434
  as is and returns Ollama's body or error ("does not support tools" moves on to the next model).
- [x] Side panel order:
  1. this computer's model
  2. the person's Mac through the relay ("Your Mac · model")
  3. the server's answer-only path
- [x] Mac SentinelGPT tab: with no folder attached and deep analysis off, a turn runs as a server
  agent turn (`SentinelAgentTurns`; `POST /admin/api/native/agent/turns`, `GET .../turns/{id}`,
  `.../stop`, `POST .../actions/{id}/confirm|undo`) that thinks on the same Mac through the relay.
  - The tab shows steps and change cards (Open/Undo, Confirm/Decline, Go/Cancel).
  - The local agent's history is kept in step.
  - Otherwise, or when that's unavailable, the tab uses its own local agent as before.
- [x] Tests (19 in `SentinelAgentTests`): relay round trip with a fake Mac, isolation, silent-Mac
  timeout, Mac-tab turns, and the JSON fields the app reads.
- [ ] Live check of the relay end to end: needs Grant's updated Mac app signed in.

## Phase C: CRM and automations (built 30-10-26)
- [x] **Acting user.** CrmService and the security audit record "who" through ICurrentUserAccessor,
  which read "unknown" in an agent turn's fresh scope. `ActingUser` (scoped) is set by the runner
  on each turn, Confirm and Undo scope; CurrentUserAccessor reads it first.
- [x] CRM (`CrmAgentTools`, Admin like the CRM pages). A company is the Company field on a contact,
  not a separate record.
  - find_contacts, read_contact (notes, deals), find_deals
  - create_contact (refuses an email already in the CRM and hands back that contact's id)
  - update_contact, add_contact_note, create_deal, update_deal, set_deal_stage (all with Undo;
    undoing an edit or stage move refuses when it has changed since)
  - trash_contact (Confirm)
- [x] Automations (`AutomationAgentTools`, Admin; the editor's per-workflow grants aren't repeated):
  - list_automations, read_automation, automation_node_types (search; no search = typeKeys by category)
  - create_automation (Confirm; checked against the registry at proposal time so mistakes go back to
    the model; connections left out chain the steps in order; made switched off; Undo deletes it
    unless it's been switched on)
  - run_automation (Confirm, no Undo), turn_on_automation (Confirm; validates and publishes first,
    like the editor's Activate), turn_off_automation (Undo)
- [x] Prompt: counts contacts/notes/deals as changes; running/switching on an automation proposes.
- [x] Tests: `SentinelAgentTests` now 22, plus the ActingUser case in `CurrentUserAccessorTests`.
- [x] User guides: Sentinel guide's SentinelGPT section, Automation guide's agentic section.
- [ ] Live check on the local sentinelgpt model (CRM scenario + "make an automation").

## Phase D: the rest (built 30-10-26)
- [x] Support (`SupportAgentTools`, Admin): find/read tickets; update_ticket (status, priority,
  assignee through ApplyTriageAsync; Undo restores the first snapshot, SLA clock included);
  tag_ticket (Undo); reply_to_ticket (Confirm: it emails the contact).
- [x] Time (`TimeAgentTools`, Admin, always the person's own): my_time, start_timer, stop_timer
  (no Undo), log_time (minutes or hours, a day); Undo deletes the entry.
- [x] Blog + content calendar (`BlogAgentTools`): posts for ContentAccess like the editor (Articles
  have no service; written directly with the editor's defaults; unique slug); Drafts only;
  publish_blog_post (Confirm; goes live + alert emails; affiliate suggestions as the editor does;
  Undo -> Draft). content_calendar + reschedule_content (Admin; Undo while the old time is ahead).
- [x] Media (`MediaAgentTools`, Admin/Contributor): find_media, set_media_alt_text (Undo).
- [x] Email (`EmailAgentTools`, Admin): send_email to a contact or address (Confirm) via
  IMailTransport; noted on the contact's activity log.
- [x] Settings (`SettingsAgentTools`, Admin): read_settings, change_setting for 7 plain values
  (Confirm, Undo).
- [x] Overwatch (`OverwatchAgentTools`, Admin): overwatch_briefing for a place (geocoded, ~30 km
  box) or the person's watched areas and trips. World News stays search_world_news.
- [x] **Tools offered by area.** 64 tools were ~23,400 characters of definitions every round of a
  16k-token context. Each toolset has a `SentinelAgentArea` (name, words, Always). A turn is
  offered the Always areas (Sentinel, research, memory) plus areas named in the request, on screen,
  in the chat's earlier messages or changes (a Go counts its plan), or used by this chat's changes.
  `use_tools` adds an area mid-turn. Offering only limits what's shown: any role-allowed tool still runs.
- [x] Tests: `SentinelAgentTests` 28 (areas, support, time, blog, calendar, email/settings/media);
  `SentinelAgentToolboxTests` resolves every toolset from AddInfrastructure and checks unique names.
- [x] User guide: Sentinel guide's SentinelGPT section.
- [ ] Live check on the local sentinelgpt model, including whether it reaches for use_tools.
