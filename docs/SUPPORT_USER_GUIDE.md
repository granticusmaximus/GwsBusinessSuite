# Support — User Guide

This is the complete guide to GWS Business Suite's Support area (`/admin/support`) and its
client-facing counterpart in the Client Portal (`/client-portal/support`) — a forum-style ticket
system for two-way conversations between your contacts and staff.

This guide is text-only (no screenshots) — see the note at the end of [`docs/USER_GUIDES.md`](USER_GUIDES.md)
for why, and how that may change later.

## Contents

1. [Core concepts](#core-concepts)
2. [The admin inbox](#the-admin-inbox)
3. [Opening a ticket on a contact's behalf](#opening-a-ticket-on-a-contacts-behalf)
4. [Replying and attachments](#replying-and-attachments)
5. [Status, priority, and assignment](#status-priority-and-assignment)
6. [Tags](#tags)
7. [Canned responses](#canned-responses)
8. [SLA targets](#sla-targets)
9. [Email notifications](#email-notifications)
10. [Automation triggers](#automation-triggers)
11. [The client's side: Client Portal support](#the-clients-side-client-portal-support)
12. [Customer satisfaction (CSAT) ratings](#customer-satisfaction-csat-ratings)
13. [Known limitations](#known-limitations)

---

## Core concepts

A **ticket** belongs to exactly one CRM **contact** and holds an ordered thread of **messages**.
Each message is tagged with an **author type** — `Contact` (the person who owns the ticket) or
`Staff` (anyone working the admin inbox) — plus an author name and timestamp. There's no
anonymous public intake: a ticket always ties back to a real `Contact` record, created either by
the contact themselves through the Client Portal or by staff opening one on their behalf from the
admin inbox.

A ticket has:

- **Status**: `Open`, `Pending` (waiting on the contact, not staff), `Resolved`, or `Closed`.
  `Resolved` and `Closed` are "terminal" — if a contact replies to a terminal ticket, it silently
  reopens to `Open` (staff replying to a terminal ticket does *not* reopen it; only the contact
  coming back to the conversation does).
- **Priority**: `Low`, `Normal` (default), `High`, or `Urgent`.
- **Assignment**: an optional free-text staff username.
- **Tags**: a free-form, comma-separated list for your own categorization.
- **SLA targets**: a first-response-due and resolution-due timestamp, computed once from
  priority when the ticket is created (see [SLA targets](#sla-targets)).
- **CSAT**: an optional 1–5 satisfaction rating plus comment, submitted once by the contact after
  the ticket is marked Resolved (see [CSAT ratings](#customer-satisfaction-csat-ratings)).

## The admin inbox

`/admin/support` (admin-only) is a two-pane inbox: a filterable ticket list on the left, the
selected ticket's full thread on the right.

- The status filter dropdown at the top of the ticket list narrows it to one status, or "All."
- Each ticket row shows its subject, contact name, priority badge, status badge, an **SLA
  overdue** badge when applicable (see [SLA targets](#sla-targets)), and its tags if any are set.
- The page header shows your average CSAT rating across whatever tickets the current status
  filter shows, once at least one ticket has been rated.
- Selecting a ticket loads its full thread, including every attachment on every message.

## Opening a ticket on a contact's behalf

Use the "New Ticket" panel in the left column: pick an existing contact, enter a subject and
first message, optionally attach files, then **Open Ticket**. A ticket opened this way starts as
`AuthorType = Staff` on its first message — this matters for two things: it doesn't email you an
admin notification about your own action (see [Email notifications](#email-notifications)), and
it doesn't reopen a since-resolved ticket the way a contact's own reply would (there's nothing to
reopen yet, since this always creates a brand-new ticket).

## Replying and attachments

Type a reply in the composer under the thread (the same WYSIWYG editor used elsewhere in the
admin suite) and select **Send**. You can attach up to 5 files per reply, 10 MB each — any file
type, not just images (screenshots, log files, documents). Attachments show as a small
paperclip-linked list under the message that carries them; clicking one downloads it directly (it
is always served as a forced download, never rendered inline in the browser, regardless of file
type — this is a deliberate security measure so an uploaded HTML or SVG file can never execute in
anyone's browser).

Only you (as an authenticated admin) or the ticket's own contact (authenticated in the Client
Portal) can ever download a given attachment — the download link checks ownership on every
request, not just once.

## Status, priority, and assignment

The three dropdowns/inputs at the top of an open ticket's thread — Status, Priority, Assign to —
apply immediately on change, no separate save step. Setting Status to `Resolved` stamps a
resolved timestamp (cleared again if the status later changes away from Resolved); it also
unlocks the CSAT prompt on the contact's side.

## Tags

The Tags field under the ticket header accepts a comma-separated list (e.g.
`billing, urgent`) and saves on blur/change. Tags are purely for your own organization and
reporting today — there's no dedicated tag filter in the ticket list yet (see
[Known limitations](#known-limitations)), but tags are visible on each ticket row so they're easy
to scan at a glance.

## Canned responses

The "Canned Responses" panel below the open ticket (visible whether or not a ticket is selected)
is a small library of reusable reply text — a macro system for answers you type often.

- **Create one**: select **New**, give it a title and body, **Save**.
- **Edit or delete**: use the buttons next to each entry in the list.
- **Insert one into a reply**: with a ticket open, use the "Insert canned response..." dropdown
  above the reply composer. Selecting one appends its body to whatever's already typed in the
  reply box (rather than replacing it), so you can lead with a personalized line and then pull in
  a standard paragraph.

Canned responses are shared across the whole admin team — there's no per-staff-member library.

## SLA targets

When a ticket is created, GWS Business Suite computes two target timestamps from its priority at
that moment (these are **not** recomputed if you change the priority later — a target that moves
after the clock has started isn't a meaningful SLA):

| Priority | First response due | Resolution due |
|---|---|---|
| Urgent | 1 hour | 4 hours |
| High | 4 hours | 24 hours |
| Normal | 8 hours | 72 hours |
| Low | 24 hours | 1 week |

Both dates show on an open ticket's header while it's Open or Pending, turning red once past due.
A ticket also picks up a red **SLA overdue** badge in the list the moment either target passes
(first response only counts as "met" once at least one Staff message exists in the thread;
resolution only counts as met once the ticket is actually marked Resolved).

Missing either target fires a **Support Ticket SLA Breached** automation trigger (see
[Automation triggers](#automation-triggers) below) — the targets themselves are still not
enforced by this app on their own; what happens after a breach fires is entirely up to the
workflow you build. See [Known limitations](#known-limitations).

## Email notifications

Every ticket action sends an email, using whatever SMTP configuration the rest of the app already
uses for growth reports and Client Portal login links — there's nothing extra to configure.

| Action | Who gets emailed |
|---|---|
| A contact opens a new ticket | You (the configured admin notify address) |
| Staff opens a ticket on a contact's behalf | Nobody (you already know) |
| Staff replies | The contact, if they have an email on file, with a link back into the Client Portal |
| The contact replies | You, with a link into the admin inbox |

A failed send (SMTP down, contact has no email on file, etc.) never blocks the ticket action
itself — the message is always saved regardless of whether the notification went out.

## Automation triggers

Three Workflow Automation trigger nodes let a workflow react to ticket activity — see
[`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md) for how to build a workflow in general:

- **Support Ticket Created** fires on every new ticket, from either side.
- **Support Ticket Replied** fires on every reply added to an existing ticket, from either side.
- **Support Ticket SLA Breached** fires once a missed [SLA target](#sla-targets) is detected by a
  background sweep that runs every 5 minutes — once for a missed first-response target (only if
  no Staff message exists yet) and once for a missed resolution target, each with the ticket id,
  contact, priority, which target was missed, and its due time as starting data. "Once" is
  literal: each of the two targets fires this trigger at most one time per ticket (tracked
  internally so the sweep never re-fires on the same breach every 5 minutes), even if the ticket
  stays overdue for days.

The two ticket-activity triggers hand the workflow the ticket id plus the relevant subject/
author/body fields as starting data, so you can, for example, notify a Slack-style webhook, tag
the CRM contact, or escalate an urgent ticket somewhere outside the inbox. A misbehaving or
misconfigured subscriber workflow can never block or delay the ticket action (or the SLA sweep)
itself — every trigger here fires only after its underlying event is already safely saved, and
any workflow failure is only logged, never surfaced to the person replying.

## The client's side: Client Portal support

Contacts reach their tickets at `/client-portal/support`, after logging in through the Client
Portal's passwordless magic-link flow (see the Client Portal guide for that login flow). The page
mirrors the admin inbox in miniature:

- **New Ticket** panel to open one, with the same optional file attachments.
- A list of their own tickets only — never anyone else's.
- Selecting one shows the full thread and a reply box, also with attachments.

A contact never sees Status/Priority/Assignment/Tags/SLA controls — those are staff-only. What
they do see, once a ticket is Resolved, is the [CSAT prompt](#customer-satisfaction-csat-ratings).

## Customer satisfaction (CSAT) ratings

Once a ticket's status becomes `Resolved`, the Client Portal shows a one-time "How did we do?"
prompt above the reply box: five star buttons (1–5) and an optional comment. Submitting it locks
in — a ticket can only be rated once, and only while it's actually Resolved (the button is
disabled until a star is picked). After submitting, the prompt is replaced with a small
"Thanks for your feedback!" summary showing the rating and comment back to the contact.

On the admin side, the page header of `/admin/support` shows your average rating and rating count
across the tickets the current status filter shows (no filter = every ticket).

## Known limitations

- **No dedicated tag filter** — tags are visible and searchable by eye, but there's no
  filter-by-tag control in the ticket list yet.
- **SLA targets trigger automation but don't enforce workflow policy.** A five-minute background
  sweep fires **Support Ticket SLA Breached** once for a missed first-response target and once for
  a missed resolution target, with ticket/contact/priority/breach-type/due-time input. What happens
  next depends on the active workflow you build; the system doesn't automatically reassign,
  escalate, or close a ticket on its own.
- **No multi-channel intake** — tickets only ever originate from the admin inbox or the Client
  Portal. There's no email-to-ticket parsing (turning an inbound email into a new ticket
  automatically).
- **Canned responses are a flat, shared list** — no folders/categories, and no per-staff-member
  personal library.
