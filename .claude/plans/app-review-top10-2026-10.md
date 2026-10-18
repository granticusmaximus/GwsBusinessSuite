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
| 2 | Billing: payment reminders, multi-currency, actions on paid/void invoices (card payments: check Stripe first) | not started |
| 3 | Support: email-to-ticket intake (reuse Sentinel email-in's IMAP mailbox approach) | not started |
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
