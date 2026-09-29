# Community — User Guide

This is the complete guide to the Community system (`/admin/community`) — the internal staff
directory, departments, activity feed, and direct messaging built into the admin app. It's an
internal intranet-style feature for staff, not anything a client or public visitor sees.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) explaining why.

## Contents

1. [Core concepts](#core-concepts)
2. [Staff Directory](#staff-directory)
3. [Departments](#departments)
4. [Member profiles](#member-profiles)
5. [Activity Feed](#activity-feed)
6. [Messages](#messages)
7. [Known limitations](#known-limitations)

---

## Core concepts

- **Member profile** — every staff account (`Admin`, `Author`, or `Contributor` role) can have a
  profile: display name, avatar, bio, job title, department, phone, and social/contact links. A
  staff account with no profile yet still shows up in the directory with sensible defaults (its
  display name is just its username, everything else blank) — a profile row is only created the
  first time someone saves it.
- **Department** — a named team (e.g. "Engineering", "Sales") with an optional description and an
  optional lead, identified by username. Departments are managed by Admins only
  (`/admin/community/departments`). Deleting a department never deletes its members' accounts or
  profiles — it just unassigns them back to "no department."
- **Editing permission** — a member can always edit their own profile. An Admin can edit anyone's
  profile. A department's lead can edit the profile of anyone in their own department. Nobody else
  can edit someone else's profile — they can only view it.
- **Activity Feed** — a running log of things staff have done elsewhere in the app: editing or
  creating a wiki page, or creating a new CRM contact. It's populated automatically by those
  features — there's nothing to configure, and no way to post to it directly.
- **Direct Messages** — one-on-one conversations between staff members. There's no group chat
  yet (see [Known limitations](#known-limitations)).

## Staff Directory

`/admin/community` is the front door: every active staff account, one card each, showing avatar
(or initials if none is set), display name, job title, and department (if any). Use the search
box to filter by name or job title, or the department dropdown to see only one team. Each card
links to that person's profile, and — for anyone other than yourself — a **Message** button that
opens (or starts) a direct-message conversation with them.

Inactive (deactivated) staff accounts never appear in the directory.

## Departments

`/admin/community/departments` (Admins only) is where teams are created and maintained. Click
**New Department** to add one with a name, an optional description, and an optional lead
(entered as that person's username). Click the pencil icon on an existing department to edit it
in place, or the trash icon to delete it — deleting only removes the department itself; its
members stay exactly as they were, just no longer assigned to any department.

A department's lead doesn't need any special role (Admin/Author/Contributor) — being named as a
lead is what grants them permission to edit their department members' profiles, nothing more.

## Member profiles

Every staff member has a profile page at `/admin/community/profile/{username}`. If you have edit
permission for that profile (see [Core concepts](#core-concepts)), you'll see an editable form for
display name, job title, department, phone, avatar URL, bio, and LinkedIn/Twitter/website links.
Otherwise you'll see the same information as a read-only summary. A **Message** button on anyone
else's profile starts a direct conversation with them.

## Activity Feed

`/admin/community/activity` shows the most recent activity across the app — currently: wiki pages
being created or edited, and new CRM contacts being created. Each entry shows who did it, what
they did, and a link to the thing itself, newest first. There's no filtering yet — it's a single
recent-activity stream.

## Messages

`/admin/community/messages` is a two-pane inbox: your conversations on the left, the open
conversation on the right. Click **+** above the conversation list to start a new one with any
teammate. Messages you haven't read yet show an unread-count badge on their conversation; opening
a conversation marks it as read. Type in the box at the bottom and press Enter (or click the send
button) to send — Shift+Enter adds a line break instead of sending.

New messages and updated unread counts appear automatically every few seconds — there's no need
to refresh the page, though there may be a brief (a few seconds) delay before a teammate's new
message shows up.

## Known limitations

- **No push notifications.** New messages and activity are delivered by the page quietly
  re-checking every 5 seconds while you have it open — nothing pings you if the tab isn't open, and
  there's no email/desktop notification for a new direct message.
- **No group conversations.** Direct messages are one-on-one only.
- **One department per person.** A staff member can belong to at most one department; there's no
  way to be on two teams at once.
- **No file attachments in messages.** Direct messages are plain text only.
- **Activity Feed has two sources today.** Wiki page saves and new CRM contacts are the only
  actions that currently post to the feed — other actions elsewhere in the app (comments, support
  tickets, billing, etc.) don't yet.
