# Booking calendar sync (28-10-26)

The last open item from `app-review-top10-2026-10.md` phase 1: "calendar sync (Google/iCloud busy
times)", listed in the CRM guide's known limitations.

## Approach

No OAuth app or API keys: both directions use iCal addresses.

- **In**: a calendar's private iCal address (Google's secret address, an iCloud public calendar,
  Outlook's published ICS) is stored encrypted on `BookingCalendarFeed`, read through an
  SSRF-guarded client (`AutomationDestinationValidator`), parsed with Ical.Net 5.2.3 (repeats,
  EXDATE, moved instances, time zones), and kept as merged busy ranges (`BusyJson`, yesterday to
  91 days ahead). A background service reads every feed every 10 minutes; a failed read keeps
  the last busy times and records the reason. Booking pages never wait on a calendar.
- **Out**: `/bookings/calendar/{token}.ics`, anonymous, `public-read` rate limit, confirmed
  bookings from the last 60 days on; the token is stored hashed (lookup) and encrypted (to show
  the link again).
- Slots exclude busy times with the type's buffer, and bookings of every type with the same
  owner (types with no owner are one diary) - before this, two booking types could be booked
  at the same moment.

## Status

**Done 28-10-26.** `BookingCalendarFeed` + `BookingCalendarSettings` (migration
`AddBookingCalendarSync`), `BookingCalendarIcs`, `BookingCalendarService` +
`BookingCalendarBackgroundService`, `BookingCalendarEndpoints`, the Calendar sync card on
Scheduling (`BookingCalendarSync.razor`). Found while checking live and fixed: Google marks every
all-day event free (holidays too), so "All-day events block" overrides TRANSP for all-day events
only; an empty 404 on the admin host re-executes to a login redirect, so the feed's 404 carries a
body; `.min-w-0` (21 uses in 16 components) was never defined, so long text pushed row buttons out
of their cards - now a global utility in app.css.

Tests: `BookingCalendarTests` (13). Live: Google's public US holidays feed added via `webcal://`
removed Election Day, Veterans Day and Thanksgiving from a weekday booking type's slots; a
non-calendar page and a private address were refused with their reasons; the bookings feed
served `text/calendar` anonymously and a wrong token got a plain 404; no phone overflow, no
console errors.
