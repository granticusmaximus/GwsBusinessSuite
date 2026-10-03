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

People can belong to **several departments**: a primary one (shown first) plus any others ticked
under **Also a member of** on their profile. Every membership counts: the directory filter, the
member counts, a lead's right to edit profiles, and **department sharing in Sentinel** (a page or
database shared with a department is open to all its members - see the Sentinel guide's *Sharing
and permissions*).

## Member profiles

Every staff member has a profile page at `/admin/community/profile/{username}`. If you have edit
permission for that profile (see [Core concepts](#core-concepts)), you'll see an editable form for
display name, job title, departments, phone, work email, avatar URL, bio, and
LinkedIn/Twitter/website links. The work email is also where unread-message reminders go (see
[Messages](#messages)); untick "Email me about messages I haven't read" to stop them.
Otherwise you'll see the same information as a read-only summary. A **Message** button on anyone
else's profile starts a direct conversation with them.

## Activity Feed

`/admin/community/activity` shows the most recent activity across the app: wiki pages created or
edited, new CRM contacts, articles and website pages going live (from any editor), support tickets
opened and resolved, and new bookings. Each entry shows who did it, what
they did, and a link to the thing itself, newest first. There's no filtering yet — it's a single
recent-activity stream.

## Messages

`/admin/community/messages` is a two-pane inbox: your conversations on the left, the open
conversation on the right. Click **+** above the conversation list to start one:

- **Direct** — a one-on-one conversation with a teammate (reopens the existing one if you already
  have it).
- **Group** — name it, tick the people to include, and **Create group**. In a group, the header
  shows how many people are in it, **Add people** brings more in (they see the history, but none of
  it counts as unread for them), and **Leave** takes you out.

Type in the box at the bottom and press Enter (or click send); Shift+Enter adds a line break. The
paperclip attaches files - up to 5 per message, 10 MB each. Images show as previews; other files
download. Only people in the conversation can open its attachments.

**Getting notified.** New messages arrive instantly in any open Messages page and in the bell at
the top right (one entry per conversation with unread messages; opening it marks it read). If a
message is still unread 15 minutes later, you get one email about it at the work email on your
profile (one per conversation until new messages arrive) - provided email delivery is set up
(Settings > Email) and you haven't turned these reminders off.

## Known limitations

- **No browser/desktop push notifications** - the bell and email reminders cover it instead.
- **Groups have no admins** - anyone in a group can add people, and there's no way to remove
  someone else (they can leave themselves).
