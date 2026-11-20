# Full-app gap sweep (20-11-26)

Grant: analyse the whole app for gaps, plan, implement. Evidence: production warnings and errors
over 24 hours (grouped by source), code markers (TODO/FIXME/NotImplemented: none found), and every
open plan item (all but stale headings were on-screen checks for Grant).

## Gaps and fixes

1. [x] **World News couldn't read compressed feeds** (UN News: "0x1F is an invalid character" = gzip
   bytes). The world-news HTTP client now decompresses. Guard `HttpClientConductTests`.
2. [x] **Eventbrite's client posed as a desktop Chrome** to get past its challenge page (against
   AGENTS.md). Honest User-Agent; its 405 refusal is respected by RefusedHostPauseHandler. Guard.
3. [x] **congress.gov 429s** (shared DEMO_KEY) warned three times a refresh: a 429 now pauses the
   Federal Civic Feed for an hour and is said once (`RefreshFailed`).
4. [x] **Perry, GA calendar timed out** 15 times a day waiting for network idle; it waits for the
   page's content instead (45 s).
5. [x] **Log noise**: World News English processing on a dropped AI connection (109/day, all retried)
   is Information; Windy's routine "inactive webcams skipped" is Debug.
6. [x] (Earlier today) KartaView host and CDN images; NWS outside the US; Mac page-start script.
7. [x] Stale camera-plan headings ticked (the phases were done or decided on 19-11-26).

## Not code (Grant)

- Mail delivery NOT SET UP in production (no SMTP route / Google account connected): forms,
  campaigns, bookings and portal sign-in links can't email. Connect a Google account in Mail.
- gov.georgia.gov refuses the server (403): respected; its feed stays empty.
- Optional: `CongressApi__ApiKey` lifts the congress.gov limit.
- On-screen checks listed in the other plans.
