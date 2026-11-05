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
- [ ] 1 Continuation + ledger + honest ending
- [ ] 2 Verification
- [ ] 3 Background turns
- [ ] 4 Coverage matrix and gaps
