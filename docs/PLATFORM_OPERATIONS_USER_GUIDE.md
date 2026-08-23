# Platform Operations — User Guide

This is the complete guide to the "Platform Operations" cluster of GWS Business Suite: the
Home dashboard (`/admin`), Mission Control (`/admin/mission-control`), the Builder Reference
Library (`/admin/cms-knowledge`), Live Show (`/admin/live-show` and `/admin/live-show-recordings`,
plus the anonymous viewer at `/watch/{token}`), Docker Management (`/admin/docker-health`),
Security Audit (`/admin/security-audit`), Privacy Operations (`/admin/privacy-operations`), and
Settings (`/admin/settings`). These are the pages that run the suite itself — day-to-day
awareness, infrastructure, broadcast, and compliance — rather than any one line of business like
CRM or the CMS.

This guide is text-only (no screenshots) — this environment currently cannot capture new
screenshots because every login now requires MFA, which blocks scripted browser capture. Every
section below is prose and tables only.

## Contents

1. [Core concepts](#core-concepts)
2. [The Home dashboard](#the-home-dashboard)
3. [Mission Control](#mission-control)
4. [The Builder Reference Library](#the-builder-reference-library)
5. [Running a Live Show broadcast](#running-a-live-show-broadcast)
6. [Past shows and recordings](#past-shows-and-recordings)
7. [Docker Management](#docker-management)
8. [The Security Audit trail](#the-security-audit-trail)
9. [Privacy Operations](#privacy-operations)
10. [Settings](#settings)
11. [Known limitations](#known-limitations)

---

## Core concepts

This cluster spans two access levels:

- **Home** (`/admin`) is open to any authenticated portal user — Admin, Author, or Contributor.
  What it shows is role-scoped: everyone sees publishing-related tiles, Contributor and above see
  site design and podcasts, and only Admin sees the operational tiles (CRM follow-ups, system
  alerts, Relationships, Intelligence, Affiliate revenue, Sentinel, Operations).
- Every other page in this guide — Mission Control, the Builder Reference Library, Live Show,
  Docker Management, Security Audit, Privacy Operations, and Settings — is **Admin-only**.

Two of these pages are deliberately **not their own source of truth**: Mission Control and the
Home dashboard's "Needs attention" cards both read live from the same services that back their
own dedicated pages (Automation, CRM, Affiliate Analytics, Docker Health) rather than maintaining
any independent count or history. If a number looks wrong, the fix is always on the page that
actually owns that data, not here.

Security Audit and Privacy Operations are closely linked: nearly every action taken in Privacy
Operations (a request created, an identity verified, a retention policy changed, a purge run) is
written as its own entry into the Security Audit ledger, under the `SecurityOperations` category.

## The Home dashboard

`/admin` is the landing page after login — "your work at a glance." It has three parts:

- **Header** — a greeting ("Good morning/afternoon/evening") based on the server's local time,
  today's date, and three quick actions: **Manage posts** (`/admin/article-editor`), **Generate
  draft** (`/admin/content-studio`), and **Quick Note**, which opens a modal that saves directly
  into an auto-indexed Quick Notes folder in Sentinel.
- **Needs attention** — a row of live counters, each a link to the page that resolves it:
  - **Drafts to review** — content pending editorial review, links to Content Studio.
  - **Comments** — comments waiting for moderation, links to `/admin/comments`.
  - Admin only: **Follow-ups** — due or overdue CRM follow-ups, links to `/admin/crm`.
  - Admin only: **System alerts** — unread Docker container health alerts, links to Docker
    Management; the tile turns red once the count is above zero.
- **Pick up where you left off** — a grid of module tiles you can jump into. Every role sees
  Publishing; Contributor and Admin also see Site design and Podcasts; Admin additionally sees
  Relationships (CRM), Intelligence, Affiliate revenue, Sentinel, and Operations (which links to
  Docker Management).

These counts are computed once, the first time the dashboard loads for your session, and are not
re-queried automatically afterward — see [Known limitations](#known-limitations).

## Mission Control

`/admin/mission-control` is a single-glance cross-cutting view: "is anything on fire right now,"
without opening Automation, CRM, Affiliate Analytics, and Docker Management separately. It shows
four clickable cards, each a live read from the module that owns the number:

| Card | What it shows | Navigates to |
| --- | --- | --- |
| Automation failures | Count of the 5 most recent failed automation runs, across every workflow | `/admin/automation` |
| Open CRM deals | Open deal count, total pipeline value in USD, and due follow-up count | `/admin/crm` |
| Affiliate clicks | Total affiliate clicks and total commission amount | `/admin/affiliate-analytics` |
| Container health alerts | Count of unread Docker health alerts | `/admin/docker-health` |

The Automation failures and Container health alerts cards get a colored left border (red and
amber respectively) whenever their count is above zero. Below the cards, a **Recent automation
failures** panel lists each of those failures by workflow name, run mode, and a truncated error
message, with a timestamp — clicking one opens that workflow's editor directly. A **Refresh**
button in the header re-pulls the whole snapshot, and a small "Generated" timestamp at the bottom
shows exactly when the data on screen was pulled.

Nothing on this page is computed or stored independently — it is a read-only composite. Fixing a
number here means fixing (or investigating) the underlying page.

## The Builder Reference Library

`/admin/cms-knowledge` — labeled **Builder Reference** in the nav — is a small internal knowledge
base: clean-room workflow notes (explicitly *not* copied source or assets from any product) that
the CMS page builder's own editor searches for capability suggestions while you're building a
page. This page itself is where you maintain that content; the actual "ask it while building a
page" experience lives inside the CMS builder editor elsewhere.

**Try a search** at the top runs the same lookup the builder editor uses: type a phrase and click
**Search**. Matching is a simple weighted keyword scorer, not semantic search — each entry gets
points for a case-insensitive substring match of your search terms in its Capability (+5 per
term), Workflow Summary (+3), Implementation Hint (+2), or Suggested Blocks list (+1), and the top
10 results are shown ranked highest score first. Zero matches shows "No matches."

Below that are two panels:

- **Sources** — a list of knowledge sources (each with a short `Key` and a display `Name`).
  **New Source** opens a blank editor with Key, Name, License Notes, and Usage Guidance fields.
  Selecting an existing source loads its editor and its entries.
- **Entries for this source** — every entry (Capability + Workflow Summary) belonging to the
  selected source, with Edit/Delete on each row, and a form below to add or edit one: Capability,
  Workflow Summary, Implementation Hint, and a comma-separated Suggested Blocks list.

**Delete Source & Entries** removes a source and every entry under it in one action, after a
confirmation prompt — there is no per-entry-only bulk delete; entries are deleted individually or
as part of their whole source.

## Running a Live Show broadcast

`/admin/live-show` — the "broadcast studio" — lets you open your camera and microphone and go
live to a small group of invited viewers over WebRTC, with the show recorded automatically. A
badge near the top shows whether a TURN relay is configured (**TURN relay configured** vs.
**Direct connection only**) — without TURN, viewers behind restrictive NATs or firewalls may fail
to connect at all, since the connection then relies on a direct peer-to-peer path.

The flow:

1. Click **Start Preview** to open your camera and microphone in the browser (a permission prompt
   appears the first time; a denial shows a specific "access was denied" message rather than a
   generic error).
2. Once previewing, use the **Mic On/Muted** and **Camera On/Off** buttons to toggle each track,
   type a **Show title**, and click **Go Live**. This starts a session record, generates a
   one-time invite link (`/watch/{token}`), configures ICE/TURN servers, and starts broadcasting.
   A **LIVE** badge appears and the invite link is shown for you to copy and share.
3. Click **End Show** to stop broadcasting (this finalizes the recording and closes the session)
   — or **Close Preview** if you back out before ever going live.

If your browser tab or connection drops without a clean **End Show** (crash, force-quit), the
server detects the broadcaster disconnecting and finalizes the recording and ends the session on
its own, so a show can't get stuck showing as permanently "Live."

**The viewer side** (`/watch/{token}`, no login required) shows the show's title and a live video
player once the token matches a currently-Live session. If the link has expired or the show
hasn't started (or has already ended), the viewer instead sees "This link isn't live right now."

## Past shows and recordings

`/admin/live-show-recordings` — **Past Shows** — lists every recording captured automatically
while broadcasting, as a grid of cards: an inline, directly-playable video, the show's title, the
date, duration, and file size. Each card has a **Delete** button that permanently removes both the
database record and the on-disk video file (tolerant of the file already being missing, so a
partially-cleaned-up recording can still be removed). Recordings are never deleted automatically —
see [Known limitations](#known-limitations).

## Docker Management

`/admin/docker-health` — nav label **Docker** — manages every container on the droplet and
bridges into DigitalOcean for droplet-level actions. It has several distinct areas.

**DigitalOcean Connection.** Paste an API token (write-only — once saved it's never shown again;
the field's placeholder tells you whether a token is already on file) and, optionally, a Droplet
ID (auto-detected in production if left blank). Once connected, the card shows the droplet's name,
status, region, size, vCPU/memory/disk description, and public IP, plus four actions:

- **Reboot** — restarts the entire droplet. The confirmation dialog is explicit that this takes
  *every* container down, including this app itself, for roughly a minute.
- **Resize** — prompts for a target size slug (e.g. `s-2vcpu-4gb`) and whether to also resize the
  disk. Disk resizes are irreversible and require the droplet to be powered off first; leaving
  disk resize off does a live CPU/RAM-only resize instead.
- **Snapshot** — prompts for a name and creates a droplet snapshot; the droplet stays usable
  throughout, though it can take several minutes.
- **Terminal** — opens a full interactive SSH terminal (xterm.js) in a new browser window,
  described below.

A **Recent droplet actions** list (last 5) shows under the connection panel once any action has
been taken.

**Build App Image** rebuilds this app's own Docker image from the repo's local `Dockerfile` via
the Docker CLI. This only works when running locally with Docker installed — production deploys
happen via `docker compose up -d --build` over SSH, not from this button.

**Containers.** Below that, every container on the host is listed as a card: name, a status badge
(green for running, red **Error** if the health monitor has flagged it, grey otherwise), image,
raw status text, and restart count if nonzero. Each card has Stop/Restart (if running) or Start
(if not) and **Pull Latest** (pulls the image's latest tag without touching the running
container), plus a **View Details** link.

**Container details** (`/admin/docker-health/{name}`) adds: full status (image, health, restart
count, exit code, started/finished timestamps); a **Suggested tip** alert when one applies;
**Recent Logs**; an **Exec Console** that runs a single one-shot command inside the container via
`/bin/sh -c` and shows its output — explicitly *not* an interactive shell (press Enter or click
Run); **Action History** (who did what and when); and **Alert History** for that specific
container. Two more actions live here: **Recreate Container** (stops, removes, and recreates the
container from its own inspected config against whatever image is now cached locally — this is
how a pulled-but-not-yet-applied image update actually gets applied) and **Remove** (permanent;
the container must already be stopped, so removal is never a surprise side effect of one click).

**Durable application logs.** The **Recent Logs** panel above (and `docker compose logs`, if you're
on the droplet directly) only show what the *current* `gwssuite` container has logged since it last
started — every deploy replaces the container, which discards everything logged before that
restart. For anything that needs to survive a deploy, the app also mirrors its own logs to a
rolling file at `/app/data/logs/gwssuite-YYYYMMDD.log` inside the container — `/app/data` is the
persisted `gwssuite-data` volume, so this history survives container recreation (up to 14 retained
files; a day gets split into multiple files if it exceeds 50 MB, which counts against that 14 —
still effectively weeks of history for this app's traffic). Read it via the SSH Terminal or the
Exec Console above (e.g. `tail -n 200 /app/data/logs/gwssuite-$(date +%Y%m%d).log`, or `grep` for
a specific subsystem across all retained days).

**Health monitoring** runs automatically in the background: every 30 seconds a sweep checks every
container's state, and raises an alert the moment a container *transitions into* an error state
(not on every poll while it stays broken). The alert message is picked from the most specific
cause available: exit code 137 (out of memory), an unhealthy healthcheck, a crash loop (restart
count ≥ 5), any other nonzero exit code, or a generic state-entry message. Every alert is
persisted, pushed live to the notification bell in every open admin session immediately (no
refresh needed), and counted in both the Home dashboard's "System alerts" tile and Mission
Control's "Container health alerts" card.

**SSH Terminal.** The Terminal button opens a genuine interactive shell — not the exec console
above — backed by a stored SSH username, port, and private key (encrypted at rest; the key itself
is never re-displayed, only replaced by pasting a new one). The droplet's SSH host key is pinned
on first connect; if it ever changes, you get an explicit mismatch warning showing both the
expected and received fingerprints (this can legitimately happen after rebuilding the droplet, or
could indicate the connection is being intercepted) and must click **Trust new key and retry**
before connecting. Resizing the terminal panel reflows the view client-side only — a program that
queries terminal size on the remote shell (e.g. `stty size`, a full-screen app) may not see the
new size until you reconnect.

## The Security Audit trail

`/admin/security-audit` is an append-only, tamper-evident evidence ledger for authentication,
account administration, authorization, protected data access, data lifecycle events, integrations,
AI egress, infrastructure actions, and security operations — nine fixed categories.

**Ledger integrity.** Click **Verify chain** to re-walk every event in order, recomputing each
event's hash from its own stored fields and confirming it both matches its stored hash and links
correctly to the previous event's hash — the same tamper-evident hash-chain idea used by a
lightweight blockchain. It reports either "Verified across N event(s)" or names the first event
where verification failed and why. Writes to the ledger are serialized on the server so the chain
can never fork under concurrent activity.

**Filtering.** A free-text search box matches against the action name, target ID, or correlation
ID; dropdowns narrow by category or outcome (Succeeded / Failed / Denied). Click **Apply**. The
table paginates at 50 rows per page with Previous/Next controls.

**Reading a row.** Each row shows: timestamp and category; the action and its correlation ID
(useful for tracing everything tied to one request); the actor's username; the target type and
ID, if any; an outcome badge plus severity; and an Evidence column showing arbitrary key/value
detail pairs plus a lock icon if an encrypted network address was retained for that event.

Sensitive detail values can never end up in this ledger by accident: any detail key containing
`password`, `secret`, `token`, `key`, `cookie`, `content`, `prompt`, `body`, or `message` is
rejected outright at write time, and each event is capped at 20 detail fields.

**Export CSV** downloads the full ledger. There's no date-range or filtered export today — see
[Known limitations](#known-limitations).

## Privacy Operations

`/admin/privacy-operations` is the operational home for data-subject rights requests, retention
review, and security-incident/breach response. The page states its own scope plainly: "This
supports—not replaces—legal and compliance review." Four live metrics sit at the top: open
requests, overdue requests, notification deadlines (incidents whose 72-hour regulator-notice
window falls due within the next 24 hours and haven't been notified yet), and open incidents.

**Data-subject requests.** Record a new request by choosing a type — Access, Erasure, Correction,
or Restriction — entering the subject's username or email, and optional initial notes, then
**Record request**. Every request gets a generated number (`PR-YYYYMMDD-XXXXXX`) and a due date of
exactly one month from receipt, regardless of type (there's no per-type SLA tiering — see
[Known limitations](#known-limitations)). Overdue, incomplete requests highlight red in the table.
Working a request follows a fixed gate:

1. **Verify identity** — required before anything else can happen to the request.
2. **Export** (Access requests only, once verified) — downloads a JSON file pulling everything the
   app can find tied to that subject's username/email: account record, CRM contact record,
   comments, SentinelGPT AI run history, podcast listening progress, and any security audit
   events where they're the actor or the target.
3. **Review** — moves the request to `InReview`.
4. **Fulfill** — for every request type except Erasure, this is a direct action. For **Erasure**
   specifically, a "Data deleted" checkbox must be ticked first; its tooltip states plainly that
   this app has no automated data-deletion action — deletion happens manually, off-platform — and
   this checkbox is only your attestation that it's actually been done everywhere.
5. **Deny** — requires a reason already present in the request's notes field; the button is
   disabled until one exists.

**Security incidents and breach assessment.** Open an incident with a title, severity (Low /
Medium / High / Critical), owner, whether personal data or ePHI was involved, and a rich-text
summary. If personal data is involved, a breach-awareness timestamp is captured immediately and a
regulator notification deadline is automatically set 72 hours out (the standard GDPR breach
notification window) — a **72h due …** badge shows until you record that the regulator was
notified. Each open incident has its own controls: a Risk assessment dropdown (Pending / Unlikely
/ Likely / High), a Status dropdown (Open / Contained / Resolved — moving to Contained or Resolved
stamps that timestamp the first time it happens), a "Notify regulator" checkbox, and **Save
assessment**.

**Retention register.** The page is explicit here too: "This screen never deletes anything
directly. A category is only purged automatically, once a day, when both 'Enabled' and 'Approved'
are checked and saved." Four categories are seeded automatically the first time this dashboard
loads: Web analytics (400 days), Form submissions (730 days), Comments (730 days), and Security
audit (2,190 days / 6 years) — the last of which the server refuses to let you ever mark
Automation-Approved, regardless of what you check in the UI, protecting audit evidence from
automated deletion. For each category you can edit the retention window (1–3,650 days), the legal
basis text, and see a live "eligible for purge" count, then toggle Enabled and Approved
independently and Save. A daily background sweep (every 24 hours) then hard-deletes rows older
than the cutoff for any category that is both Enabled and Approved — currently that logic only
covers Web analytics, Form submissions, and Comments; no other data category has an automated
purge path yet, Security audit included.

Every action on this page — a request created, identity verified, status changed, an incident
opened or updated, a retention policy changed, a purge actually executed, a subject export
downloaded — is written into the Security Audit ledger under the `SecurityOperations` category at
`High` severity, so this page's own activity is itself auditable.

## Settings

`/admin/settings` is site-wide configuration, organized into six tabs down the left side.

- **General** — site name and site slug for the CMS site matching this deployment's configured
  slug. A note warns that changing the slug affects any URLs or references built from it.
- **Reading** — posts-per-page for the public `/blog` listing (10/12/25/50).
- **Writing** — default category and default author byline applied to *new* posts only; existing
  posts are untouched.
- **Media** — max upload size in MB (1–100), applied to hero images and the media library, for
  future uploads only.
- **SentinelGPT** — an Ollama model override and a generation timeout override (both blank =
  server default), a hero image generation model (feeds Content Studio's "Generate with
  SentinelGPT" button; local image generation is experimental and macOS-only today, and leaving
  this blank disables that button entirely), and the semantic knowledge index status (document
  count, model, last-indexed time) with a **Rebuild index** button.
- **Developer API** — issue scoped API keys: name, requests-per-minute rate limit, expiry
  (30/90/365 days or never), and one or more scope checkboxes. The full plaintext key is shown
  exactly once at issuance (with a copy-to-clipboard button) and is never retrievable again. Below
  that, every issued key is listed with its prefix, scopes, usage count, last-used time, rate
  limit, and status (Active/Expired/Revoked), with a **Revoke** action (confirmed) per active key.
  An API reference panel documents Bearer-token auth and the three resources currently exposed —
  Contacts, Deals, and CMS pages — with example `curl` usage.

## Known limitations

These are gaps actually observed in the current code, not hypothetical ones:

- **Home dashboard counts are computed once per session load**, not re-queried on a timer or on
  navigation back to the page — the "Needs attention" numbers can go stale while you keep working
  elsewhere and won't update until you get a fresh page load.
- **Mission Control has no drill-down of its own.** It's a fixed set of four categories with no
  historical trend, no per-item filtering beyond automation's 5 most-recent failures, and no
  independent data — every number is a live pass-through to the page that owns it.
- **The Builder Reference Library's search is keyword/substring scoring, not semantic search.**
  There's no fuzzy matching or synonym awareness — a search for a concept that doesn't share
  literal words with an entry's text won't find it.
- **Live Show has no scheduling, multi-camera/screen-share, chat/reactions, or live viewer
  count**, and recordings are never deleted automatically — disk usage only shrinks when someone
  deletes a recording from Past Shows by hand.
- **Docker Management's Exec Console is one-shot only**, not an interactive PTY — for a real shell
  you need the separate SSH Terminal feature. The **Build App Image** button only works against a
  local Docker daemon; it does nothing useful from a production container, where deploys go
  through `docker compose up -d --build` over SSH instead.
- **Security Audit's UI exposes Search, Category, and Outcome filters only** — the underlying
  query model also supports filtering by actor and by date range, but there's no UI control for
  either, and CSV export always exports the full ledger rather than the current filtered view.
- **Privacy Operations' request due dates are a flat one month for every request type** — there's
  no per-type SLA tiering (an Access request and an Erasure request get the same clock). **Erasure
  fulfillment is an honor-system attestation**, not a real action — this app has no automated
  cross-system data-deletion capability anywhere, so "Fulfilled" only records that a human
  confirmed deletion happened manually elsewhere. **Retention automation covers only three data
  categories** (Web analytics, Form submissions, Comments); Security audit is intentionally
  excluded from automated purge, and no other data category (CRM contacts, Sentinel content, etc.)
  has a retention policy or purge path at all yet.
- **Settings manages exactly one CMS site** — whichever one matches this deployment's configured
  site slug. There's no multi-site switcher or management list here.
