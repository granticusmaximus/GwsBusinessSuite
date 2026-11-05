# SentinelGPT finishes whole jobs, and checks them (05-11-26)

Grant: "there is an arbitrary number of steps ... if the task is complex and needs more than 8 steps
it will not take on the task. ... a handoff to start another set of 8 steps ... CRUD within and
without the app, based on the user's role, and get the entirety of the task completed and verified."

## What's there now (investigated 05-11-26)

- **The limit:** `SentinelAgentRunner.LocalRounds = 8` (Phase A, 306949b3, 30-10-26). One round is
  one model call plus the tool calls it asks for.
  - Every caller passes 8: the side panel on a local model, the panel through your Mac, and the
    Mac tab (`SentinelAgentTurns`).
  - It was a safety cap against local models looping and against runaway time. A relayed call may
    take up to 3 minutes, so 8 rounds could already take 24.
  - Nothing was built to carry on past it.
- **What happens at the limit:**
  - If nothing was changed: "I couldn't finish that in 8 steps. Try asking for one thing at a
    time." That's the refusal Grant sees.
  - If something was changed: **"Done: ..."**, even though the job was cut off half way. That's a
    false report of completion.
- **The two limits don't fit together.** On 02-11 the plan threshold went from 3 to 25 routine
  changes (573720d), but the 8-round cap stayed. Local models mostly make one call per round, so
  even an approved plan of more than ~7 changes can't finish.
- **The model doesn't know its budget.** The prompt never mentions it, so it can't pace itself or
  save its progress.
- **Nothing checks the result.** No step re-reads what was changed and compares it with what was
  asked.
- **The tools** (107: 36 read, 49 change, 22 Confirm) are role-filtered (`SentinelAgentUser.CanUse`:
  Admin all, otherwise each tool's roles), and the record-level access checks are from 03-11.
  - Tools that act outside the app: `search_web`, `read_web_page` (research), `send_email`, and
    `run_automation` (Confirm).

## Design

### 1. Continuation (the handoff)
- A turn runs in **legs** of 8 rounds, which keeps a local model's context small. A leg that ends
  without a final answer hands off to the next one automatically. The next leg gets a fresh
  context built from:
  - the system prompt
  - the original request
  - the **task ledger**
  - the last results
- **Task ledger**, persisted on the turn:
  - the goal and the steps (from `show_plan`, or a checklist the model keeps with a new
    `update_progress` tool)
  - each step's state
  - the actions done, with their links
  - what's left
- **Budget:** up to 6 legs (48 rounds) and 30 minutes a turn, both configurable.
- **No-progress stop:** a leg that makes no new change and moves no step forward ends the job.
- **Honest ending:**
  - "Done" only when the model says it finished *and* verification passes (2).
  - Otherwise "Partly done: 5 of 9 steps. Still to do: ...", with a **Continue** button that
    resumes from the ledger.
- The plan rule stays as it is: more than 25 routine changes shows the plan and waits for Go. The
  approved job then runs across as many legs as it needs.

### 2. Verification
- **Per change:** each change tool names the read tool that shows its result (e.g.
  `create_invoice_draft` -> `read_invoice`). After the job, every change is re-read and its fields
  compared with what the tool was asked to set. A mismatch or a missing record fails that change.
- **Whole job:** a checker pass on the same model gets the request, the ledger and the re-read
  records, and answers `{ "complete": bool, "missing": [...] }`. If something is missing, one
  repair leg does just that, then everything is checked again.
- Change cards show **Verified** or **Didn't check out: <why>**.

### 3. Long jobs carry on in the background
- Every agent turn runs through `SentinelAgentTurns`, on the server, with the browser following.
  So a 20-minute job carries on when you navigate away or close the panel, and the bell says when
  it's done. Today a panel turn lives in the panel's circuit.

### 4. Role-based reach, inside and outside the app
- A coverage matrix: each app area x Create/Read/Update/Delete x role, from the tool registry.
  Gaps get filled in priority order.
- Outside the app:
  - research (web) is read-only
  - email goes through the Gmail API route on the droplet (the SMTP block)
  - automations, webhooks and sending wait for Confirm, as now
- Server AI is off, so the model is always your Mac's (local, or through the relay). A job keeps
  running only while that Mac is reachable. The ledger lets it pick up again.

## Open questions for Grant
- Budget: 6 legs / 30 minutes by default?
- Carry on automatically between legs (proposed), or ask after each one?

## Progress
- [x] **1+2 built 05-11-26.** Grant asked SentinelGPT to "think about what the task is asking for,
  deliberate, consider an appropriate plan, execute the steps ... break it into sections ... build
  the pathway". `SentinelAgentRunner.Jobs.cs`:
  - **Think.** A no-tools call gets the request, the screen, what's done and what the tools
    returned, plus the role's capabilities. It replies with JSON: understanding, approach,
    cannot, and sections of steps, each with a check. Limits: 8 sections, 10 steps a section, 40
    in all. If it can't produce a plan, the request becomes a one-step job.
  - **Ledger.** The plan is a show_plan action; its ArgumentsJson keeps the job, and the summary
    is the checklist card (☐ ☑ ✓ ✗ –).
  - **Starts** when the model calls `plan_job` (always offered, with a prompt rule for anything
    over 3 steps), or when the fast path runs out of rounds. That replaces "couldn't finish in 8
    steps" and the false "Done: ...".
  - **Section legs.** Up to MaxRounds rounds each, with a fresh context (system prompt + brief +
    ledger), 2 legs a section, 16 legs and 30 minutes a turn. `complete_step` / `skip_step`
    move the ledger.
  - **Check, then repair.** A check leg with only the read tools replies with JSON
    {complete, problems}. Failures get one repair leg and one more check.
  - **Honest outcome:** "Done and checked", "Partly done ... Still to do ... carry on", or "I
    didn't change anything".
  - **Resume.** "carry on", or the panel's **Carry on**, continues the saved job. Go on a plan of
    more than 25 steps resumes the saved plan.
  - **Tests:** SentinelAgentTests +6 (sections, out-of-rounds escalation, repair, partial + carry
    on, Go, parser bounds); the existing 32 unchanged.
  - **Live-checked 05-11-26 on sentinelgpt:latest (gemma4)** with the opt-in
    `Live_*OnARealLocalModel` tests (`GWS_LIVE_OLLAMA_MODEL=sentinelgpt:latest`):
    - A two-list job is done on the fast path, correctly not planned.
    - The 5 lists + "review" on each job runs out of fast-path rounds after 8 changes, plans only
      the 2 left, carries them out, reads the pages back, and answers "Done and checked".
    - The first run exposed three things, all fixed:
      1. Each leg's user message was the original request, so every leg redid the whole job.
         It's now the leg's own instruction.
      2. The repeat guard was per leg, so all 5 lists were re-created. It's now seeded from the
         chat's done actions, with arguments in canonical order.
      3. The planner put doubts under "cannot". The field is now `not_possible`, with a narrow
         wording.
- [x] **3 Background turns, 05-11-26.** When the panel thinks on the person's Mac through the relay,
  the turn now runs in `SentinelAgentTurns` on the server (page context passed through), and the
  panel follows its snapshot every 0.5 s.
  - Closing the tab cancels only the following. The Stop button stops the job.
  - Get() marks the turn watched. A turn that finishes 20 s or more after anyone last polled it
    is kept as a `FinishedJob` (30 min) and raises `JobFinishedUnwatched`. The bell shows it as
    "SentinelGPT finished", linking to /admin/sentinelgpt/activity.
  - With a local model (the Mac app's bridge, or a browser Ollama allows), the turn stays in the
    circuit, because the model is reached through that page's JS. The ledger + "carry on" cover
    a closed tab there.
  - Test: SentinelAgentTests (unwatched vs followed).
- [x] **4 Coverage matrix and gaps, 05-11-26.** Mapped from the registry: 98 tools in 17 areas.
  Gaps filled, each to its page's roles:
  - **Comments**, new toolset (ContentAccess): list, set_comment_status with Undo, delete_comment
    (Confirm).
  - **Mail**, new toolset (every role, the person's own mailboxes only): list_mail, read_mail,
    draft_mail (Undo trashes the draft), send_mail (Confirm).
  - **Insights**, new toolset (Admin, read-only): growth_summary, system_health.
  - **Support:** create_ticket (Confirm, since the contact may be emailed).
  - **CRM:** read_deal, and delete_deal (Confirm).
  - **Blog:** trash_blog_post (Confirm, with Undo).
  - **Automations:** update_automation (Undo) and delete_automation (Confirm). Both need Edit on
    the workflow unless you're an Admin.
  - **Tests:** SentinelAgentCoverageTests (comment moderation + Undo, own-mailbox only + Confirm
    send, roles per area).
  - **Still not covered**, each its own product or too risky for an agent:
    - campaigns and article alerts
    - social publishing
    - Content Studio generation (Mac-only)
    - users and roles
    - privacy erasure
    - container restarts

Paused for this: tech-forward-ux T5 (charts/sparklines). `DayRange` plus the three daily-count service
methods are in place (they build, nothing calls them yet). Next: MissionControl series, Sparkline,
KpiCard/MetricCard trend, chart theme, compact offer.
