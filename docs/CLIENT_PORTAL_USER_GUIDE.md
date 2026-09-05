# Client Portal & Public Site — User Guide

This is the complete guide to what GWS Business Suite shows people who are **not** logged into
the admin app: a CRM contact using the passwordless Client Portal (`/client-portal`), and an
anonymous visitor on the public marketing/blog site (`grantwatson.dev`). It also covers two other
unauthenticated surfaces staff can hand out selectively — a Sentinel public share link and a Live
Show viewer link — and contrasts all of this with the separate, MFA-protected admin login. This
guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why.

## Contents

1. [Core concepts](#core-concepts)
2. [A client's first Client Portal login](#a-clients-first-client-portal-login)
3. [The Client Portal dashboard](#the-client-portal-dashboard)
4. [Client Portal support tickets](#client-portal-support-tickets)
5. [Viewing a Sentinel public share link](#viewing-a-sentinel-public-share-link)
6. [Watching a Live Show broadcast](#watching-a-live-show-broadcast)
7. [Navigating the public marketing site](#navigating-the-public-marketing-site)
8. [Admin login, for contrast](#admin-login-for-contrast)
9. [Known limitations](#known-limitations)

---

## Core concepts

GWS Business Suite has three separate identity worlds, each with its own cookie scheme, and
they're never interchangeable:

- **Staff accounts** — an admin/Author/Contributor username and password, protected by mandatory
  multi-factor authentication (MFA) on every login, with access to `/admin/*`. See
  [Admin login, for contrast](#admin-login-for-contrast).
- **CRM contacts** — a row in the CRM's `Contact` table. A contact never has a username or
  password. If they have an email on file and staff has pointed them at `/client-portal/login`,
  they can sign in with a one-time emailed link (see below). There's no contact self-registration
  anywhere — a Contact record only ever comes from staff creating one (directly, via CRM import,
  or via a public form submission that a workflow turns into a contact).
- **Anonymous visitors** — anyone on the public site or a share link, with no account of any kind.

A **magic link** is the Client Portal's entire authentication mechanism: a single-use, 15-minute
token emailed to the contact's address on file, in place of a password. A **Sentinel public
share** is a separate, unrelated mechanism — a token-bearing URL that staff generates from inside
Sentinel (the internal wiki) to expose one page, database, or automation status page to anyone
with the link, with no contact record or login involved at all. Both are distinct again from the
**public marketing site**, which is CMS-driven content with no login concept for visitors
whatsoever.

## A client's first Client Portal login

There is no separate "sign up" step. A client's very first Client Portal visit and every
subsequent one work the same way:

1. The client goes to `/client-portal/login` and enters their email address.
2. The server looks up a `Contact` with that email (case-insensitively). Whether or not a match
   was found, the page shows the identical message: *"If that email matches an account, a
   sign-in link is on its way. Check your inbox - the link expires in 15 minutes."* This is
   deliberate — the app never confirms or denies that a given email address belongs to a real
   contact.
3. If a contact does match, the app emails a link like
   `https://.../client-portal/auth/consume?token=...` using the same SMTP configuration the rest
   of the suite already uses (support-ticket notifications, growth reports).
4. Clicking the link consumes the token (marking it used immediately, even if something later
   fails) and signs the visitor in under a separate `ClientPortal` cookie scheme, valid for 14
   days, carrying only their name and their `ContactId` as claims. They land on `/client-portal`.
5. If the token is missing, already used, or older than 15 minutes, the link instead bounces back
   to `/client-portal/login?error=invalid` with *"That link is invalid or has expired. Request a
   new one below."* — there's no way to "resend" a specific link; the client just requests a new
   one from the login page.

Because a contact never sets a password, there is nothing to reset and nothing to remember —
every login is a fresh emailed link. `/client-portal/auth/logout` signs them out and returns them
to the login page.

## The Client Portal dashboard

Once signed in, `/client-portal` (the "My Account" page) is the landing page and — as of this
guide — the only real content page in the portal besides Support. It shows, scoped strictly to
that one contact's own `ContactId`:

- **Deals** — every CRM deal tied to the contact, as cards showing title, pipeline stage, value,
  and expected close date (when set). If the contact has no deals, the page says "Nothing to show
  here yet" instead of an empty grid.
- **Invoices** — only invoices in the `Sent` status (i.e., issued and awaiting payment). A
  `Draft` invoice doesn't exist to the contact yet, and `Paid`/`Void` invoices have nothing left
  for them to act on, so both are deliberately left out of this view. Each visible invoice shows
  its title, total, due date, and — when Stripe has generated one — a **Pay now** button linking
  out to Stripe's own hosted invoice page.

The header also picks up the branding of whatever CMS site is configured first in the system: its
logo (if set) and accent color, so the portal visually matches the operator's own site rather than
looking like a generic back-office screen. From the header, the client can jump to **Support** or
**Sign out**.

## Client Portal support tickets

`/client-portal/support` is the client-facing half of the support ticket system — a contact can
open a new ticket, see only their own tickets, reply with attachments, and rate a resolved ticket.
This already has its own complete guide — see
[`SUPPORT_USER_GUIDE.md`](SUPPORT_USER_GUIDE.md) (section "The client's side: Client Portal
support") for the full walkthrough; it isn't repeated here.

## Viewing a Sentinel public share link

A Sentinel public share is how staff hand an outside party (a client, a partner, anyone without a
GWS login) read-only access to one specific piece of Sentinel content — a wiki page, a database,
or an automation's public status page — without giving them any broader access to the suite.

**How staff create one:** from inside Sentinel (or, for a workflow, from that workflow's settings
tab), a **Share** panel lets staff:

- Invite a specific existing staff username with View/Comment/Edit/Full Access, for internal
  sharing — unrelated to the public link.
- Create a **public link**, optionally with an expiry date/time and/or a password. Submitting
  this mints a token and shows the full share URL (`/sentinel/share/{token}`) exactly once, with
  an explicit warning to copy it immediately — the underlying secret is stored hashed, so the app
  itself can never show the plaintext token again after that first display.
- See every public link created for that target (revoked ones hidden), including its view count
  and last-viewed time, and revoke any of them at any time.

**What the visitor sees at `/sentinel/share/{token}`:** the page is fully unauthenticated (no
Client Portal or staff login involved). It resolves the token and then behaves as follows:

- An invalid, expired, or revoked token shows "Link unavailable — This share link is invalid,
  expired, or revoked."
- A password-protected link shows a password prompt first; an incorrect password shows "Incorrect
  password" and lets the visitor try again, with no attempt limit visible in the page itself.
- Once unlocked (or if no password was set), the visitor sees a clean, branded read-only view
  ("SENTINEL" watermark) of exactly one of three things, depending on what was shared:
  - **A wiki page** — its title, icon, cover image (if set), and its full rendered content
    (including rich content like equations/code blocks, rendered the same way the live editor
    renders them).
  - **A database** — its title and icon, then every row rendered as a small card: the row's title
    property plus every other non-empty property as a `Name: value` line. There's no filtering,
    sorting, or interactivity — it's a flat, ordered read-out of the whole database.
  - **An automation's public status page** — the workflow's name, description, current
    Active/Inactive status, last-run time, and a table of recent runs (status, start time,
    duration). It never exposes node configuration, credentials, or any run's actual input/output
    data. See [`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md) ("Public status pages") for
    how staff sets this one up specifically.
- Every successful view (after any password check) is recorded, which is what feeds the view
  count/last-viewed time staff sees back in the Share panel.

## Watching a Live Show broadcast

Live Show is a small built-in broadcast studio: staff can turn on their camera/microphone from
`/admin/live-show`, click **Go Live**, and get a single shareable invite link to hand to a small
group of viewers — no external streaming service involved.

**How staff starts a broadcast:** from `/admin/live-show`, **Start Preview** opens the camera and
mic locally (with mute/camera-off toggles), then **Go Live** starts a session and immediately
shows an invite URL in the form `https://.../watch/{token}` for staff to copy and send out. Shows
are recorded automatically; **End Show** stops the broadcast (also finalizing the recording), and
past recordings live under `/admin/live-show-recordings`.

**What the viewer sees at `/watch/{token}`:** this is a fully unauthenticated page — no Client
Portal or admin login of any kind.

- If the token doesn't correspond to a session that's currently live (wrong link, show hasn't
  started yet, or the show already ended), the viewer just sees "This link isn't live right now —
  The invite link may have expired, or the show hasn't started yet." There's no waiting-room or
  auto-retry; they'd need to reload once the broadcaster actually goes live.
- Otherwise, the page shows the session title and video element, and attempts to connect to the
  live WebRTC session; if that fails outright, it shows "Could not connect to the broadcast."
- If the broadcaster ends the show while someone is still watching, that viewer sees "The show
  just ended."

The invite link is tied to that one session's token — there's no separate viewer allow-list,
password, or expiry beyond "the show is currently live"; anyone who has the link while the show is
live can watch.

## Navigating the public marketing site

The public marketing/blog site (`grantwatson.dev` and `www.grantwatson.dev`) is entirely separate
from the Client Portal and from staff logins — it has no visitor accounts of any kind. It's built
from the same "Pages"/page-editor CMS builder documented in
[`SITE_BUILDING_USER_GUIDE.md`](SITE_BUILDING_USER_GUIDE.md); this section only covers what a
visitor experiences when browsing the finished result, not how staff builds a page.

- **Home page** (`/`) renders the Canvas page whose slug is `home` on the configured site. On any
  other host (the admin domain, localhost, a direct IP) the same root path instead redirects to
  `/admin` — the marketing home page only ever appears on the actual public host.
- **Individual CMS pages** are served from a catch-all route, so nested paths (e.g.
  `services/web-dev`) resolve directly, matching however staff nested them in the page tree.
  Visitors only ever see **published** pages; a draft page 404s for an anonymous visitor (an
  authenticated Contributor/Admin previewing their own draft is the one exception).
- **Blog** lives at `/blog` (a paginated list with keyword/category/tag filters) and each article
  at `/blog/{slug}`. Only articles that are `Published` *and* whose publish time has actually
  arrived are visible — a future-dated scheduled article isn't shown early, and an unpublished or
  trashed slug 404s with a normal branded "Article not found" page.
- **Forms**: any CMS page can include a "form" widget (e.g. a contact page, a booking-request
  page) with admin-defined fields. Submitting one is a plain POST with an invisible honeypot field
  and its own tighter rate limit; on success the visitor is redirected back to the same page with
  `?submitted=1`. Each field can optionally be mapped to an identity role (Email/Full Name/
  Company/Phone) in the page builder, which lets a submission feed a real CRM Contact instead of
  staying an opaque JSON blob: the widget has its own "Create CRM contacts from this form's
  submissions" opt-in (off by default, matches by email so a repeat submitter updates the same
  Contact rather than creating duplicates), staff can create/link a Contact manually from the raw
  submission view, and a `cms.formSubmittedTrigger` Automation node fires on every submission for
  building a workflow on top. **Turning on auto-create effectively also grants Client Portal
  access** — any Contact with a matching email can request a magic-link login (see below), so a
  form that auto-creates Contacts makes every submitter a potential portal user, not just a CRM
  record.
- **Blog comments**: each article can show a comment form. A submitted comment is *not* shown
  immediately — the article page only ever displays comments that have already been approved
  (`ListApprovedForArticleAsync`); after submitting, the visitor is redirected back to the article
  with a "your comment is awaiting approval" banner. There's no visitor login or identity beyond
  the name/email they typed into the form for that one comment.
- Standard machine-readable endpoints exist too: `/sitemap.xml`, `/robots.txt`, `/rss.xml`
  (and `/feed` as an alias) — all built from the same published-article set the blog list uses.

**What a visitor can't do**: create an account of any kind, log in, see a draft or scheduled page
early, see an unapproved comment (their own or anyone else's), or edit/delete anything they
submitted. Nothing on the public site grants access to the Client Portal, Sentinel, or the admin
app — those are entirely separate authentication surfaces.

## Admin login, for contrast

Staff sign in at a different page entirely — `/admin/login` — with a username and password, not
an email-based magic link. Unlike the Client Portal, **every admin login requires MFA**: after a
correct username/password, the account moves to a pending-MFA state and must complete a
verification step before a real session is issued; there's no dev bypass for this in the running
app. Admin sessions are also subject to lockout after repeated failed attempts and general login
rate-limiting, both of which surface as specific error messages on the login page (too many
attempts, account locked for N minutes, verification session expired). None of this applies to
Client Portal contacts, who never have a password to get wrong in the first place, and whose only
"failure mode" is an expired or already-used link.

## Known limitations

- **No password option for contacts.** The Client Portal is magic-link-only; there's no
  fallback login method if a contact's email is unreachable or wrong, other than a staff member
  fixing the email on the Contact record.
- **No explicit self-service contact registration.** There's still no dedicated "sign up for
  portal access" flow — a Contact record comes from staff creating one, a CRM import, an
  Automation workflow, or (now) a public form widget with its "Create CRM contacts" opt-in
  enabled. That last path means a visitor filling out such a form becomes a real Contact — and
  therefore gains Client Portal access — as a side effect, without a dedicated registration step
  or any portal-specific confirmation.
- **Client Portal dashboard is read-only and narrow.** It shows deals and open invoices only —
  there's no self-service invoice history, document library, or profile/contact-info editing for
  the client themselves.
- **Sentinel public share links have no visible attempt limit** on the password prompt shown in
  the page itself, and no per-viewer identity — view counts and "last viewed" are aggregate, not
  attributable to who looked.
- **Live Show has no waiting room or notification.** A viewer who arrives before the broadcaster
  goes live just sees "not live right now" with no way to be notified when it starts; they have to
  reload manually.
- **Public form submissions have no built-in follow-up.** Beyond saving the submission and an
  optional Automation trigger a staff member has to build separately, there's no automatic
  visitor-facing confirmation email or ticket creation tied to a form submit.
- **Blog comment moderation has no visitor-facing status page.** A visitor who submits a comment
  sees a one-time "awaiting approval" banner on redirect, but has no way to check back later on
  whether their specific comment was approved or rejected.
