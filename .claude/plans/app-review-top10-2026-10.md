# App review: top-10 shortlist (started 18-10-26)

Source: the "GWS Suite App Review" report (https://claude.ai/artifact/3DDhLvvV9abk6KzMBkm2Uf),
which compares each admin page with 5 market alternatives. Grant said "Proceed" on its Top 10.
Each phase is a finished, verified unit; the order puts contained gap fixes first.

**Check before building:** the report was written from the user guides, not a full code read.
Grep for each item first. Two hints already turned up:
- `ContactTimelineService` exists (phase 7 may be partly built).
- The CRM guide mentions Stripe for invoices (phase 2's card payments may exist).

| # | Phase | Status |
|---|-------|--------|
| 1 | Scheduling: per-type time zone, visitor time zones, days ahead / notice / max per day, slot re-check on booking | done 18-10-26 |
| 2 | Billing: multi-currency, reminders (manual + automatic), view/duplicate on sent/paid/void, line order | done 18-10-26 |
| 3 | Support: email-to-ticket (new mail -> ticket, tagged replies join, staff replies emailed with text + Reply-To) | done 18-10-26 |
| 4 | Site: duplicate page, nested menus, hreflang + language switcher | not started |
| 5 | Automation: expression functions + sandboxed transform step | not started |
| 6 | Media: file storage instead of base64-in-DB, resizing, WebP/AVIF | not started |
| 7 | Customer timeline across apps (check ContactTimelineService first) | not started |
| 8 | Business Intelligence: plain-English questions, local model, read-only | not started |
| 9 | One SentinelGPT side panel with page context | not started |
| 10 | Nav merge + admin shell redesign (workspace rail, inspector) | not started |

## Phase 1 notes (done)

- `BookingType`: `TimeZoneId` (blank = server zone via `TimeProvider.LocalTimeZone`), `DaysAhead`
  (1-90), `MinNoticeMinutes` (min 15), `MaxPerDay`. `Booking.AttendeeTimeZone`.
- Migration `AddBookingTimeZonesAndRules`: existing types get 14 days / 60 min, matching the old
  fixed behaviour.
- Behaviour change for existing types: their windows used to be read as UTC. They're now read in
  the server zone (New York), so a 09:00-17:00 window means 09:00 New York, as the editor always
  implied.
- Public page detects the browser zone (`wwwroot/js/booking-timezone.js`) and has a picker.
  Emails name the zone.
- Tests: `BookingTimeZoneTests`, covering DST gap, rules, refusing times not on offer, and the
  attendee's zone. The existing `BookingServiceTests` clock is pinned to UTC.
- Still open: calendar sync (Google/iCloud busy times), noted in the CRM guide's limitations.

## Phase 2 notes (done)

- Card payments were already there: Stripe hosted invoice pages, with a webhook marking them paid.
- `InvoiceCurrencies` (Application/Billing) holds 18 currencies and their decimals, used for Stripe
  minor units (JPY = whole units) and for display (`EUR 1,234.50`). `UnitPriceUsd`/`TotalUsd` keep
  their names but are in the invoice's currency.
- Reminders: `Invoice.AutoRemind/RemindersSent/LastReminderAt`. `IStripeInvoicingClient.ResendInvoiceAsync`
  (Stripe's send-invoice email again). Manual sends allowed once per 20 h. Automatic: a day overdue,
  weekly, max 3 (`InvoiceReminders.MaxAutomatic`), via `InvoiceReminderBackgroundService` hourly.
- Fixed along the way:
  - Invoice lines had no stored order: they came back in random GUID order. `InvoiceLineItem.Position` fixes it.
  - Line Add/remove buttons in the Billing and Scheduling editors defaulted to type=submit inside the EditForm.
  - A Stripe error on Send would crash the circuit; now it shows as a message.
- Migration `AddInvoiceRemindersAndLineOrder`.

## Phase 3 notes (done)

- `SupportEmailIn` (Application/Support) holds the rules: ticket tag `[Ticket #<first 8 hex>]`,
  subject cleanup, automatic-mail detection, quoted-reply stripping.
- `SupportEmailInService`: own mailbox (`SupportMailboxSettings`), log table (`SupportInboundEmail`,
  unique MessageId), polled every 2 minutes by `SupportEmailInBackgroundService`.
- IMAP sign-in shared with Sentinel via `ImapMailboxConnector`.
- Security rule: only a DMARC/DKIM-verified sender who is the ticket's contact can add to a ticket.
  Unverified mail still becomes a new ticket, tagged `unverified-sender`, and gets no receipt.
- Staff replies now email the reply text (`TicketReplyEmail` on `IClientPortalEmailSender`), with
  the tag, and with Reply-To set to the mailbox while it's on. Before, the email only had a portal
  link.
- Panel: Support page, "Email-to-ticket" button (`SupportEmailInPanel.razor`).
- Migration `AddSupportEmailToTicket`.
