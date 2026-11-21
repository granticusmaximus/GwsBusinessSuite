# Mail: inbox tabs sorted by GWS, folders and mail rules (21-11-26)

## What Grant asked for
- Gmail's Primary / Promotions / Social / Updates / Forums tabs and its sidebar, as a GWS version.
- "Not just copy them": GWS itself sorts mail into the tabs with correct filtering, on any mailbox.
- Sorting: **rules first, SentinelGPT (AI) only for the messages the rules can't place.**
- Moving a message to another tab moves it **and remembers the sender** (undoable).
- Folders, and **mail rules** that file certain emails into those folders.

## Constraints
- Grant's Gmail is connected over **IMAP** (imap.gmail.com); Gmail's own categories can't be changed
  over IMAP, so the tab is GWS's decision, kept in GWS. Gmail's category is not used as a signal.
- Nothing from the mailbox is stored except what sorting needs: per message, its Message-ID key and
  the tab (and why); per sender, a learned tab. No bodies.
- Gmail over IMAP: a "move" out of the inbox is a label change (X-GM-LABELS), never a MOVE into
  All Mail (the MOVE NO "System Error (Failure)" fixed on 21-11-26).
- AI through IOllamaService (Mac first, AI droplet backup), background priority, never blocking a list.

## Phases
1. **Sorter (Application, pure):** `MailSorter.Classify(headers)` -> tab + how sure + why, from
   List-Id / List-Post / List-Unsubscribe / Precedence / Auto-Submitted / Feedback-ID / ESP headers,
   sender (no-reply, notifications), known social networks, receipt/shipping/security wording.
   Unit tests with real-world header sets.
2. **Tabs on every mailbox:** backends fetch the inbox's sorting headers (IMAP: one FETCH of header
   fields; Gmail API: metadata). A tab lists the inbox filtered by GWS's decision. Decisions and
   learned senders in new tables (migration). Counts and newest senders per tab.
3. **AI for unclear mail:** background job asks SentinelGPT for the tab of messages the rules
   marked unclear (subject, sender, headers only); saved, shown on next refresh; unclear mail sits in
   the rules' best guess meanwhile.
4. **Move to tab:** right-click "Move to tab", drag onto a tab; remembers the sender; Undo; a list of
   learned senders to remove.
5. **Folders:** create, rename, delete (IMAP folders; Gmail labels), "Move to folder" on messages.
6. **Mail rules:** conditions (from, to, subject, words, list) -> actions (move to folder, tab, mark
   read, star, archive). Runs on new inbox mail in the background, plus "Run on existing mail".
   Rules editor in Mail.

## Progress
- 21-11-26: Gmail-category tabs and the Gmail-style sidebar built first (folding rail, More, label
  colours, tabs bar with counts and senders); superseded in phase 2 by GWS sorting.
  Also fixed: Gmail archive used MOVE into All Mail (NO "System Error (Failure)"); now removes the label.
- 21-11-26 Phases 1-4 done. MailSorter (rules, sure/unclear, reason) + MailSorterTests; tabs on every
  mailbox (backends' InboxForSortingAsync: IMAP one FETCH of the sort headers, Gmail API metadata; newest
  MailTabs.SortWindow = 400; MailInboxCache 90 s, dropped on any change); MailMessageTab / MailSenderTab
  (migration AddMailTabs); MailTabAiService + MailTabAiBackgroundService (every 2 min, background priority,
  10 at a time, sender + subject + reason only, subject cleared after; unclear rows dropped after 14 days);
  Move to tab (right-click) remembers the sender, Undo moves back and forgets; "Senders sorted into
  tabs" dialog in the mailbox menu. Gmail-style sidebar: folding rail, More, label colours. Visual check:
  demo backend in a throwaway worktree (1440 and 390). Earlier Gmail-category tab code removed.
  Next: phase 5 (folders), phase 6 (mail rules).
- 21-11-26 Shipped 64415b0d (phases 1-5). Live check: migration applied; 103 unclear messages queued from
  Grant's inbox; SentinelGPT decided its first 10 at 19:11 (10 per 2 minutes). Ownership fix before the
  inbox cache (two-user test, negative control on a scratch copy). Phase 5 (folders) checked on screen with
  a demo backend: create, Move to folder + Undo, rename (stays open), delete with confirm. Drag onto a tab
  not built (right-click only). Next: phase 6, mail rules.
- 21-11-26 Phase 6 done: MailRule / MailRuleSeen (migration AddMailRules), MailRuleEngine (fields, ops,
  validation, run order: flags, tab, then folder or archive), RunRulesAsync (first pass only records the
  inbox; first match wins; each message once; seen rows pruned after 60 days), MailRulesBackgroundService
  (every 3 min, as each owner), Mail rules dialog (list with switches, editor, Run on inbox now, Make a
  rule for ...). Checked on screen with a demo backend; fixed the all/any select showing blank (bool
  bind). All six phases done; only drag-onto-a-tab not built.
