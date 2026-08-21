# CRM & Relationships — User Guide

This is the complete guide to GWS Business Suite's "CRM & Relationships" cluster: CRM
(`/admin/crm`), Deal Scoring (`/admin/deal-scoring`), Billing (`/admin/billing`), Scheduling
(`/admin/scheduling`), Email Campaigns (`/admin/email-campaigns`), Comments (`/admin/comments`),
and User Management (`/admin/users`). It also covers the small set of public-facing pages these
features hand out to visitors — the booking page (`/book/{slug}`), the booking management link
(`/book/manage/{token}`), the public comment form on a blog post, and a campaign's unsubscribe
link — since understanding those is part of understanding the admin side.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why, and how that may change later. Support tickets
are a related but separate feature with their own full guide:
[`SUPPORT_USER_GUIDE.md`](SUPPORT_USER_GUIDE.md).

## Contents

1. [Core concepts](#core-concepts)
2. [Managing a contact](#managing-a-contact)
3. [Working the deal pipeline](#working-the-deal-pipeline)
4. [Understanding a deal score](#understanding-a-deal-score)
5. [Creating and sending an invoice](#creating-and-sending-an-invoice)
6. [Setting up a bookable meeting type and viewing bookings](#setting-up-a-bookable-meeting-type-and-viewing-bookings)
7. [Building an email drip campaign](#building-an-email-drip-campaign)
8. [Moderating a comment](#moderating-a-comment)
9. [Managing a user account and role](#managing-a-user-account-and-role)
10. [Known limitations](#known-limitations)

---

## Core concepts

- **Contact** — the one shared identity every other feature in this cluster hangs off of. A
  contact has a name, optional email/company, a **Status** (`Lead`, `Prospect`, `Customer`,
  `Inactive`), an optional follow-up date, and a soft-delete Trash (contacts are never
  permanently gone until you explicitly empty Trash). Invoices, deals, bookings, and campaign
  enrollments all reference a contact; a booking made on the public page will create a new
  contact automatically if the attendee's email doesn't already match one.
- **Deal / pipeline stage** — a sales opportunity tied to exactly one contact. A deal moves
  through six stages: `Lead → Qualified → ProposalSent → Negotiation`, then closes as `Won` or
  `Lost`. The first four are "open" and shown on the Kanban-style pipeline board; `Won`/`Lost` are
  terminal and shown in a separate closed list. A contact can have several deals over time.
- **Invoice** — a billing record tied to a contact (and optionally one of their deals), composed
  of line items (description, quantity, unit price). An invoice has a lifecycle: `Draft` (local
  only, freely editable) → `Sent` (created in Stripe, no longer editable) → `Paid` (set
  automatically by a Stripe webhook) or `Void`.
- **Booking type vs. booking** — a **booking type** is a meeting template you configure (title,
  duration, buffer, weekly recurring availability) that lives at a public URL,
  `/book/{slug}`. A **booking** is one visitor's actual reservation against that template's open
  slots, holding their name, email, and the confirmed start/end time.
- **Campaign** — a drip/nurture email sequence: a name plus an ordered list of **steps** (subject
  + body + a delay in days from the previous step, or from enrollment for step 1). A contact is
  **enrolled** in a campaign and receives its steps automatically, one at a time, as each one's
  delay elapses. A campaign is `Draft` (can't be enrolled into), `Active` (enrolling and sending),
  or `Paused` (existing enrollments hold in place; nothing sends until resumed).
- **Role** — every admin account has exactly one role: `Admin`, `Author`, or `Contributor`.
  Everything in this cluster except Comments requires `Admin`. Comments moderation is open to all
  three roles (`Admin`, `Author`, or `Contributor`) since it's treated as a content-moderation task
  rather than an account-administration one.

## Managing a contact

`/admin/crm` opens on the **Contacts** tab: a searchable/filterable list on the left, an
editor and activity log on the right.

- **Create one**: click **New Contact**, fill in Full Name (required), Status, Email, Company,
  and an optional Follow-up Date, then **Save Contact**.
- **Search and filter**: the search box matches name, email, or company (case-insensitive,
  substring match); the status dropdown narrows the list to one `ContactStatuses` value or "All
  statuses."
- **Edit one**: click a contact in the list to load it into the editor, change fields, **Save
  Contact** again. You can't edit a contact that's been trashed since another tab or teammate
  moved it there — you'll get an error telling you to restore it first.
- **Trash / Restore / Delete Permanently**: **Move to Trash** soft-deletes the currently-open
  contact (with a confirmation prompt). The **Trash** button switches the whole left panel into a
  trash view, from which you can **Restore** a contact back to the main list or **Delete
  Permanently** (both are separately confirmed — permanent delete really does remove the row, and
  every invoice/deal/booking that still points at it keeps the dangling reference rather than
  cascading).
- **Follow-ups**: if any non-trashed contact has a Follow-up Date that's today or earlier, a
  warning banner lists them all at the top of the page (across both tabs) with a one-click link
  back into that contact.
- **Notes & Activity**: once a contact is open, the "Notes & Activity" panel underneath the editor
  lets you log a free-text note (type it and press **Log** or hit Enter) — a simple, append-only,
  timestamped audit trail of calls/emails/notes for that contact. Entries are never edited or
  deleted, only added, newest first.

The Contacts list and follow-up banner are cached for 30 seconds server-side for performance, so
a change you just made elsewhere may take up to that long to show on a page you already had open;
reloading the page always shows current data.

## Working the deal pipeline

Switch to the **Pipeline** tab (also shown as a count badge on the tab itself). The top of the tab
is a deal editor; below it is a four-column Kanban board (one column per open stage) plus a
**Closed** list for `Won`/`Lost` deals.

- **Create a deal**: pick a Contact (you need at least one contact first — the form disables
  itself and shows a warning if there are none), give it a Title, pick a Stage, a Value (USD), and
  optionally an Expected Close Date and Notes, then **Save Deal**.
- **Move a deal between stages**: use the small stage dropdown on the deal's card directly on the
  board — this is the fast path and doesn't require opening the editor. Moving into `Won` or
  `Lost` stamps a `ClosedAt` timestamp; moving a deal back out of either (re-opening it) clears
  that timestamp again.
- **Edit a deal's other fields** (title, value, close date, notes): click the pencil icon on its
  card to load it into the editor above, change what you need, **Save Deal**.
- **Delete a deal**: the trash icon on its card, with a confirmation prompt — this is a hard
  delete with no Trash/undo, unlike contacts.
- Every stage change (from either the dropdown or the editor) can fire a **CRM Deal Stage
  Changed** Workflow Automation trigger for any workflow subscribed to it — see
  [`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md) — except a stage change made *by* an
  automation action node itself, which is deliberately excluded to avoid an automation
  re-triggering itself in a loop.

## Understanding a deal score

`/admin/deal-scoring` is a read-only page — there's nothing to configure or click besides
expanding a deal's factor breakdown. It scores every currently **open** deal (the same four
stages as the pipeline board) fresh on every page load; nothing is stored.

At the top, a baseline strip shows three numbers computed from your own historical `Won`/`Lost`
deals: how many closed deals you have on record, your historical win rate, and the average number
of days a `Won` deal took to close. If you have zero closed deals yet, every deal starts from a
neutral baseline score of 50 instead.

Each open deal gets a 0–100 score and a band — **Hot** (≥70), **Warm** (≥45), **Cool** (≥25), or
**Cold** (below 25) — built from named, visible factors (click the chevron on a deal to expand
them). The scoring is a deliberately simple, explainable heuristic over *your own* historical
data — not a trained machine-learning model, and the page says so:

| Factor | How it's computed |
| --- | --- |
| Historical win rate (or Baseline) | Your `Won ÷ (Won + Lost)` percentage across all closed deals, rounded — or a flat 50 if you have no closed deals yet to learn from. |
| Engagement | Based on the most recent CRM Activity logged for the deal's contact: **+15** if within the last 7 days, **+5** if within 30 days, **−5** if older than that, **−10** if no activity has ever been logged for that contact. |
| Pace | Only scored when you have at least one `Won` deal to derive an average time-to-win from: **+10** if the deal's current age is at or under that average, **−15** if it's run more than double that average. (A deal whose age falls *between* the average and double the average gets no Pace factor at all — it's not being penalized or rewarded either way.) |
| Close date | **+5** if the deal's Expected Close Date is still in the future, **−10** if it's already passed. No factor at all if no close date was set. |

The total is clamped to 0–100 (factors can't push a score negative or above 100). There's no way
to override a score manually, no historical trend of how a deal's score has changed over time, and
no action buttons on this page — it's purely informational, meant to be read alongside the
Pipeline tab, not a replacement for it.

## Creating and sending an invoice

`/admin/billing` drafts invoices locally and, when you're ready, sends them through Stripe's
hosted invoice page — card numbers never touch this app directly.

If `Stripe:SecretKey`/`Stripe:PublishableKey` aren't configured, a warning banner says so and
every **Send** button is disabled; you can still draft invoices freely in the meantime.

- **Draft an invoice**: pick a Contact, optionally one of their deals, a Title, an optional Due
  Date, and one or more line items (description, quantity, unit price — each needs a non-empty
  description and a quantity/price both greater than zero). **Save Draft**. The running total
  updates live as you add/remove line items.
- **Edit a draft**: click the pencil icon on any `Draft`-status invoice in the list on the right,
  change it, **Save Draft** again — only a `Draft` can be edited this way; once sent, an invoice's
  content is frozen.
- **Send**: click **Send** on a `Draft` invoice (disabled if Stripe isn't configured, or if the
  invoice has no line items). The first time you ever send an invoice to a given contact, a Stripe
  Customer is created for them and reused on every later invoice; the invoice's line items are
  sent to Stripe as-is, and the status flips to `Sent`. If you set a Due Date, it's converted to a
  "days from send" figure Stripe actually uses (minimum 1 day; 14 days if you left Due Date
  blank) — so the due date you see after sending may shift slightly from what you originally
  typed, since it's now anchored to the actual send moment rather than the day you drafted it.
- **Void**: a `Sent` invoice (not yet paid) can be **Void**ed, which also voids it on the Stripe
  side if it made it there.
- **Delete**: only a `Draft` can be deleted outright — void a sent invoice instead; there's no
  confirmation prompt before deleting a draft, unlike trashing a contact or deleting a comment.
- **Paid**: nothing you do in this UI marks an invoice Paid — that status is set automatically
  when Stripe's `invoice.paid` webhook arrives, safely idempotent against Stripe's own webhook
  retries.

Every invoice is priced in USD (`usd`) — there's no currency selector in the editor even though
the underlying record has a currency field.

## Setting up a bookable meeting type and viewing bookings

`/admin/scheduling` manages **booking types** on the left and shows every **booking** made
against any of them on the right.

- **Create a booking type**: give it a Title (required), an optional URL slug (auto-generated
  from the title if you leave it blank — lowercased, non-alphanumeric runs collapsed to a single
  `-`, and de-duplicated with a `-2`, `-3`, … suffix if that slug is already taken), a Description,
  a Duration and Buffer in minutes, an optional Owner username (a display label shown to visitors
  and in confirmation emails — not tied to a real account), and whether it's Active (bookable) at
  all.
- **Weekly availability**: click **Add** to add a recurring weekly window (day of week + start/end
  time, all in UTC — this app is single-tenant and doesn't do per-visitor timezone conversion, so
  "UTC" here means whatever your team has agreed it means). A booking type with no availability
  windows at all shows no open slots to visitors. **Save** writes the type.
- **The public page**: each booking type gets a stable link, shown right under its title in the
  list (`{your domain}/book/{slug}`) — share this directly with prospects. It always shows the
  next 14 days of open slots (this is fixed, not something you configure per type), computed by
  walking each weekly window in `(duration + buffer)`-minute steps and excluding anything already
  confirmed-booked, with at least a 1-hour lead time before the earliest bookable moment (so
  nobody can book a meeting starting sooner than anyone could plausibly see the confirmation
  email). A visitor picks a slot, enters name/email/optional notes, and confirms; the slot is
  re-validated for overlap at that exact moment (not just trusted from what the page showed a
  minute earlier) to close the race between two people booking the same slot at once. A
  confirmed booking emails the attendee a confirmation with a private manage/cancel link
  (`/book/manage/{token}`) they can use to cancel it themselves later, no login required.
- **Edit or delete a booking type**: the pencil/trash icons in the Booking Types list. Deleting a
  booking type has no confirmation prompt and permanently deletes every booking ever made against
  it along with it — there's no way to delete just the type while keeping its booking history.
- **Bookings list**: every booking across every type, newest-first, with a **Cancel** button (also
  with no confirmation prompt) that emails the attendee a cancellation notice. A booking's contact
  is found by matching the attendee's email against an existing Contact, or a brand-new Contact is
  created automatically if there's no match — so the booking page is itself a lead-capture form.

## Building an email drip campaign

`/admin/email-campaigns` manages campaigns on the left and, once you select one, its enrollments
on the right.

- **Create a campaign**: give it a Name and Description, then **Add Step** for each email in the
  sequence. Step 1 always sends immediately on enrollment; every step after that sends some number
  of **Delay (days)** after the *previous* step (not after enrollment) — so a 3-step sequence with
  delays of 3 and 4 sends on day 0, day 3, and day 7. Each step needs a Subject and a Body; the
  body supports two merge tokens, `{{FirstName}}` (the contact's first name, derived by splitting
  their full name on spaces) and `{{FullName}}`. **Save**.
- **Activate it**: a brand-new campaign starts as `Draft`, which accepts no enrollments at all.
  Click **Activate** to flip it to `Active`. **Pause** freezes every current enrollment's next-send
  time in place rather than cancelling anything — resuming (Activate again) picks up exactly where
  it left off, it doesn't restart the sequence.
- **Enroll a contact**: with the campaign selected, pick a contact from the dropdown and click
  **Enroll**. This silently no-ops (with a message telling you why) rather than erroring if the
  contact is already enrolled in that campaign, has globally unsubscribed from every campaign, or
  the campaign isn't `Active`.
- **Watch enrollments**: the table under the campaign shows each enrolled contact's status
  (`Active`/`Completed`/`Cancelled`), which step they're on out of the total, and their next send
  time. A background sweep (running every 5 minutes) is what actually sends due steps — there's no
  manual "send now" button, so a step you'd expect to fire immediately can take up to 5 minutes.
- **Unsubscribing**: every sent step includes an unsubscribe link unique to that contact (a
  non-expiring, encrypted token — not a stored, expirable code — so it keeps working no matter how
  old the email is). Clicking it sets a global "unsubscribed from campaigns" flag on the contact
  (suppressing them from *every* campaign, not just the one the email came from) and cancels every
  campaign they're currently actively enrolled in. There's no in-app UI for a contact to manage
  this themselves beyond that one link, and no admin-side "resubscribe" button — you'd have to
  clear the flag directly if a contact asked to opt back in.
- **Delete a campaign**: the **Delete** button, with no confirmation prompt, permanently removes
  the campaign, its steps, every enrollment, and every send-log entry for it.

## Moderating a comment

`/admin/comments` moderates visitor comments left on published blog articles before they appear
publicly. Unlike everything else in this cluster, this page is available to `Author` and
`Contributor` accounts too, not just `Admin` — treated as a content task rather than an account
one.

Every comment a visitor submits (through the comment form on a live article) starts life as
`Pending` — nothing is ever auto-approved. The submission endpoint also has a honeypot field and
its own rate limit, and silently no-ops for anything a bot fills in, so a batch of obvious spam
generally never even reaches this queue looking like a real submission.

- **Filter**: the tab strip (All / Pending / Approved / Spam) narrows the table; each non-"All" tab
  fetches only that status server-side rather than filtering a client-side copy of everything.
- **Moderate one at a time**: the row-actions under each comment's excerpt let you **Approve**,
  mark **Spam**, or **Unapprove** (send an `Approved`/`Spam` comment back to `Pending` for
  re-review — there was previously no way back once a comment left `Pending` short of deleting
  it), and **Delete**. Only the actions relevant to a comment's *current* status are shown (e.g. an
  already-`Approved` comment doesn't show an "Approve" link).
- **Bulk actions**: check the boxes on the left of several rows and a toolbar appears above the
  table with **Approve**, **Mark Spam**, and **Delete**, applied to every selected row at once.
- **Threading**: a comment can be a reply to another (visitors can only reply to an already-
  `Approved` parent). Replies show nested and indented under their parent with a "Reply to
  {name}" badge. **Deleting a parent comment doesn't delete its replies** — they're re-parented
  up to whatever the deleted comment's own parent was (or promoted to top-level if it had none),
  so a moderation delete never silently wipes out an entire reply thread.
- Only `Approved` comments are ever shown on the public article page; `Pending` and `Spam`
  comments are never visible to visitors regardless of who else replied to them.

## Managing a user account and role

`/admin/users` (Admin-only) creates and manages the accounts that can sign into this admin portal
at all — CRM/Billing/Scheduling/Email Campaigns/Deal Scoring/User Management itself all require
the `Admin` role specifically; Comments additionally allows `Author`/`Contributor`.

- **Create a user**: **New User**, enter a Username, an initial Password, and a Role
  (`Author`/`Contributor`/`Admin`), then **Create User**. The username must be unique, and the
  password is rejected if it's under 12 characters, equal to the username itself
  (case-insensitively), or one of a short list of commonly-guessed passwords (`password`,
  `admin`, `changeme`, etc.) — with the specific reason shown back to you rather than a generic
  "invalid password."
- **Change a role**: **Edit Role** on a user's row, pick the new role from the inline dropdown,
  **Save**. Blocked if this would leave the account with zero active `Admin`s — the very last
  active Admin can't demote themselves (or be demoted) out of that role from this UI.
- **Reset a password**: **Reset Password**, type the new one inline, **Set** — subject to the
  same password-strength rule as account creation. This is also the one action that clears any
  standing lockout on the account as a side effect, on the theory that an admin actively resetting
  a password is already a deliberate recovery action.
- **Activate / Deactivate**: toggles whether the account can sign in at all, without deleting it.
  Same last-Admin protection as role changes — you can't deactivate the only active Admin. There's
  no separate "delete user" action anywhere in this UI; deactivating is the only way to revoke an
  account.
- **Lockout and Unlock**: five failed password attempts in a row locks the account out for 15
  minutes (a locked-out account shows a small "Locked until {time}" badge next to its status, and
  further login attempts are rejected immediately without even checking the password, so a
  lockout can't be used as a timing oracle). An admin can clear a lockout early with **Unlock**
  without touching the account's password.
- **MFA**: every account has mandatory TOTP-based multi-factor authentication, enrolled by the
  user themselves the first time they sign in (this page shows whether MFA is enabled per user but
  doesn't let an admin enroll or reset MFA on someone else's behalf — see
  [Known limitations](#known-limitations)). Every account-administration action taken from this
  page (user created, role changed, password reset, account unlocked, activated/deactivated) is
  recorded into the suite's tamper-evident security audit trail, visible under Operations →
  Security Audit (its own guide is planned as part of a future Platform Operations guide, not
  duplicated here).

## Known limitations

These are real gaps observed directly in the current code, not a general disclaimer:

- **Deal Scoring is read-only and can't be tuned.** There's no way to reweight a factor, override
  a score, or see how a deal's score has trended over time — it's fully recomputed from scratch on
  every page load, nothing is persisted.
- **Invoices are USD-only.** `Invoice.Currency` exists on the record and defaults to `usd`, but the
  editor has no currency selector.
- **A `Paid` or `Void` invoice has no actions at all in the UI** — not even a link back to its
  Stripe hosted page. Only `Draft` (edit/send/delete) and `Sent` (view/void) rows show any buttons.
- **Deleting a booking type cascades silently.** It permanently deletes every booking ever made
  against that type, with no confirmation prompt and no way to keep the booking history while
  removing just the type.
- **The public booking page always looks 14 days ahead**, and isn't configurable per booking type
  from this UI (the service itself supports up to 60 days).
- **Scheduling times are UTC-only with no per-visitor timezone conversion** — by design, for a
  single self-hosted team, but worth knowing if you ever book with someone in a very different
  timezone.
- **No campaign send-time control beyond the day-delay chain.** There's no "send only during
  business hours" or "skip weekends" option, and the background sweep only runs every 5 minutes,
  so there's no true "send immediately" button.
- **Campaign unsubscribe is global, not per-campaign**, and there's no admin-side "resubscribe"
  control — a contact who unsubscribes is suppressed from every campaign, and getting them back in
  requires clearing the flag directly rather than through this UI.
- **Several delete/void actions across Billing, Scheduling, and Email Campaigns have no
  confirmation prompt** (unlike Contacts and Comments, which do) — Delete Draft, Delete Booking
  Type, Cancel Booking, and Delete Campaign all act immediately on click.
- **Comment moderation has no spam-pattern filtering beyond the honeypot field** — every non-bot
  submission lands in `Pending` for a human to review; there's no keyword blocklist or third-party
  spam-scoring integration.
- **User Management can't administer another user's MFA.** An admin can see whether MFA is
  enabled for an account and can Unlock/reset its password, but there's no admin-side "reset MFA"
  or "disable MFA" action — a user who loses both their authenticator and every recovery code has
  no self-service or admin-assisted recovery path in this UI today.
- **No per-workflow or per-record sharing model in this cluster** — every feature here is either
  fully Admin-gated or fully open to Admin/Author/Contributor; there's no way to grant one
  Contributor access to just one campaign or one booking type the way Workflow Automation lets you
  share a single workflow (see [`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md)).
