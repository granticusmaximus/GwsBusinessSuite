# Mail, more like Gmail (06-11-26)

Grant: "There seems to be no easy way to mass select and mass delete emails ... I would also like to
right click on the email(s) itself and have a menu pop up that states something like Mark Read/Unread
and then a Delete function ... trackpad gestures when I swipe right or left over an email message, I
can mark a message read or unread, and the other swipe could be a delete function ... the ability to
load/add multiple email accounts to this workflow ... how would you make this more Google Gmail like
in the UI?"

Found first: several mailboxes per person already worked (`MailLimits.MaxAccounts` 10), but "Add a
mailbox" sat at the foot of the folder column, the switcher only appeared with two, and on a phone
the whole account area was hidden.

## Phase 1 - actions on many messages (service and both backends) - DONE
- `MailActions`: read, unread, star, unstar, archive, trash, delete-forever.
- `IMailService.ApplyAsync(ids, action)` returns `MailActionResult(Count, Undo)`; `UndoAsync(undo)`.
- Gmail: `messages/batchModify` (up to 1,000 ids) for labels; Trash is `messages/{id}/trash`,
  the documented call (first one alone so a sign-in renewal happens once, then 4 at a time);
  Undo is `untrash`, or INBOX added back after Archive. Delete forever is `batchDelete`.
- IMAP: one sign-in, flags or a move on the whole UID set; the move's UID map (UIDPLUS) is the Undo.
  In Trash, delete is for good (flag Deleted, expunge those UIDs).
- At most 500 at a time.

## Phase 2 - the list, Gmail-style - DONE
- Toolbar: select-all box (with All, None, Read, Unread, Starred, Unstarred), Refresh; with a
  selection: Archive, Delete, Mark read, Mark unread, Star, and "N selected".
- Rows: box, star, sender, subject and snippet, date; on hover, Archive, Delete and Read/Unread
  replace the date. Shift-click selects a range. With no message open the list takes the reading
  pane's room and rows go to one line, as in Gmail.
- Every change shows at once and goes back if the server refuses. Archive and Delete leave an
  Undo bar (bottom centre, 8 s). Delete forever (in Trash) asks first.

## Phase 3 - right-click menu - DONE
- On a row: Open, Reply, Forward; Mark read/unread, Star/Unstar; Archive, Delete; Find emails from
  the sender. On a row in a selection it acts on the selection. Keyboard: the Menu key or Shift+F10
  on a row; arrows, Enter, Escape.

## Phase 4 - swipes - DONE
- Two-finger trackpad swipe (horizontal wheel) or a touch drag on a row: right marks read or unread,
  left deletes (Apple Mail's sides). The row follows the fingers over a coloured action; past the
  threshold it commits when the gesture ends, otherwise it springs back. Vertical scrolling is left
  alone, and the browser's back gesture doesn't fire over the list. `wwwroot/js/mail-list.js`.

## Phase 5 - several mailboxes - DONE
- An account switcher at the top of the folder column, on every screen size: each mailbox with its
  unread count, Add another mailbox, Edit, Remove.
- Compose has a From picker when there are two or more mailboxes (a reply goes from the mailbox the
  message is in).

## Phase 6 - Gmail's keyboard shortcuts - DONE
- j/k, o or Enter, u, x, s, e, #, Shift+I, Shift+U, r, a, f, c, /, and ? for the list of them.

## Checked (06-11-26)
- Against a real IMAP server: GreenMail 2.1.2 in Docker, IMAPS behind the trusted localhost dev
  certificate, 14 + 3 seeded messages, the app in WebKit (Playwright):
  - Connect.
  - Shift-click range, Mark read (unread 5 -> 4 on the server).
  - Right-click menu: arrows; flips to stay on screen at the corner; Escape.
  - Star. Swipe left (delete, Undo puts it back through the UIDPLUS map). Swipe right (unread).
  - A vertical scroll isn't a swipe. j/x/#/?/o/u.
  - Select all -> Delete 13 -> Undo -> Delete again.
  - Trash: Delete forever asks, then 14 deleted. The server agreed: Inbox 0, Trash 0.
  - Second mailbox: the switcher shows unread counts; the From picker. Phone width: no sideways scroll.
- Fixed on the way:
  - GreenMail reported a wider COPYUID range than was moved, so new UIDs are looked up per message.
  - Typing SMTP port 465 now means TLS from the start; only the presets used to set it.
  - Deleting the message under the keyboard cursor moves the cursor on.
  - Unread counts load on every mailbox switch.
- Not checked live: Gmail (needs Grant's Google sign-in); batchModify/trash/untrash/batchDelete
  calls are covered by MailServiceTests against a stub.

## Later, if wanted (not built)
- All inboxes in one list; Gmail's Primary/Promotions/Social tabs; conversations grouped into
  threads; snooze; swipe actions chosen per person.
