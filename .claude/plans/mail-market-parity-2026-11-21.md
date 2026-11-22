# Mail market-parity implementation (21-11-26)

## Goal

Inventory the Mail section, compare it with current Gmail, Outlook, Yahoo Mail and Proton Mail,
then implement a contained provider-neutral parity unit without weakening the existing live-mail
privacy model.

## Current inventory

- Gmail OAuth and generic IMAP/SMTP accounts, with up to ten private mailboxes per person.
- Inbox, Starred, Sent, Drafts, Archive, Spam and Trash; custom Gmail labels / IMAP folders.
- GWS Primary, Promotions, Social, Updates and Forums tabs on every provider, with rules-first
  classification, SentinelGPT fallback, learned senders, drag/drop and Undo.
- Search, paging, periodic refresh, unread counts, bulk selection/actions, right-click actions,
  keyboard shortcuts and mobile swipe actions.
- Read, sanitized HTML, remote-image privacy, attachment download, reply, reply-all and forward.
- Compose with To/Cc/Bcc, Markdown formatting toolbar and preview, multiple attachments, drafts,
  multi-account From selection and threading.
- User-created mail rules, manual rule runs, account setup presets and responsive three-pane UI.

## Market findings

- Gmail, Outlook, Yahoo Mail and Proton Mail all expose schedule-send workflows.
- Gmail-style and modern Outlook/Proton workflows expose snooze/reappear-later.
- Signatures are baseline composition settings across the major providers.
- Attachment reminders are a low-friction safeguard (documented by Proton and common in mature
  composers).
- Priority/focused views and filters for unread, starred and attached mail are standard discovery
  tools.

## This implementation unit

1. Persist one signature per connected mailbox and expose an accessible editor from its account
   menu. Apply it to new messages, replies and forwards without duplicating it in saved drafts.
2. Before send, detect common attachment wording when no file is attached and require an explicit
   Send anyway decision. Forwarded attachments count as attached.
3. Add responsive list filters for All, Unread, Starred and Attachments. Filtering acts on the
   currently loaded provider results and clearly reports the filtered count.
4. Add narrow service/component tests, update the Mail user guide, run the full release gate, and
   visually validate desktop, tablet and phone layouts plus every new interaction.

## Deliberately separate next unit

- Schedule send and snooze need durable server-side state, edit/cancel behavior, retry/error
  handling and background processing while the page is closed. They should be implemented together
  as a queue/reminder unit rather than simulated with browser timers. Confidential-mode semantics,
  read receipts and cloud-link attachments are provider-specific and need separate product decisions.

## Durable scheduling and snooze unit (22-11-26)

1. Save scheduled outgoing mail as a real provider draft; keep only its provider ID, ownership,
   subject/recipient display metadata, due time and retry state in GWS.
2. Run a durable background dispatcher with bounded retries. Let the person list scheduled mail,
   cancel it back to Drafts, and see a delivery problem without losing the draft.
3. Snooze inbox mail by archiving it with the provider's undo token, then restore it at the chosen
   time. Let the person list and wake snoozed mail early.
4. Add accessible desktop/phone controls, migrations, service/background tests, guide updates,
   focused browser proof and the full release gate.

Provider-specific items remain explicit: Gmail confidential mode, Microsoft sensitivity/recall,
provider read receipts and cloud-drive link permissions cannot be made reliable through generic
IMAP/SMTP. They will not be represented by controls that silently do nothing on some accounts.

## Progress

- [x] Current feature inventory completed from the page, services and regression tests.
- [x] Official provider research completed.
- [x] Signature persistence and UI.
- [x] Attachment reminder.
- [x] Message filters.
- [x] Focused tests and user guide (56 focused regression tests passed).
- [x] Full release gate: all local checks passed; PARTIAL only because deployed/responsive targets
  were not supplied to the script.
- [x] Responsive and interaction visual validation at 1440, 768 and 390 px; representative
  browser proof is under `output/playwright/mail-market-parity-*.png`.
- [x] 22-11-26 responsive rework after review: narrow folder items are constrained to compact
  horizontal chips, the account switcher collapses to its avatar, inbox tabs remain an equal
  five-column grid, and signature/attachment confirmations are validated as true modal overlays.
  Final phone proof: `output/playwright/mail-market-parity-phone-final.png`.
- [ ] Durable scheduled-send queue and management UI.
- [ ] Durable snooze/wake queue and management UI.
- [ ] Queue tests, migration, guide, responsive proof and release gate.
