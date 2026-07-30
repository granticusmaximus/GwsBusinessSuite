# Growth Studio

Growth Studio is GWS Business Suite's first-party audience analytics and social publishing
workspace at `/admin/growth`. It takes its product cues from Plausible's focused dashboard,
Matomo's data ownership and segmentation, and Google Analytics' event/conversion model. It is
a clean-room GWS implementation; it does not copy their source, branding, or proprietary
algorithms.

## Delivered foundation

- Cookieless public-site page views, engagement time, custom events, referrer host, UTM
  acquisition dimensions, device category, and browser family. A random first-party browser
  identifier is stored in local storage with a rolling 90-day expiry so returning visits can
  be measured without cookies, IP storage, fingerprinting, or third-party analytics requests.
- No raw IP address, full user-agent, full referrer URL, query string, or admin-route activity
  is stored. Do Not Track and Global Privacy Control disable the browser collector.
- Indexed, server-side date filtering with 7/30/90-day ranges, real-time visitor count,
  audience trend, top pages, acquisition, campaigns, devices, bounce rate, and engagement.
- `window.gwsAnalytics.track("event-name")` for first-party custom events.
- Named conversion goals for exact custom events, exact destination pages, or page-path
  prefixes ending in `*`, with active/paused state, conversion counts, unique converting
  visitors, session conversion rate, and top acquisition source. Removing a goal never
  removes the underlying analytics history.
- Two-to-eight-step funnels built from ordered custom events and page destinations, with
  per-step session counts, continuation rates, drop-off counts/rates, and overall completion.
  Editing or removing a funnel changes only its definition, not the event history.
- Saved audience segments with one-to-five AND rules across page path, event, source, medium,
  campaign, referrer, device, and browser dimensions. Applying a segment filters the complete
  visit, so dashboard metrics, engagement, goals, and funnels remain internally consistent.
  Segment definitions can be edited or removed without changing historical analytics events.
- New-versus-returning visitor reporting and daily/weekly retention cohorts, including
  cohort sizes and period-by-period retained visitor rates. Selected audience segments apply
  consistently to retention activity while new/returning status reflects the browser's actual
  first recorded visit.
- Country and region reporting from a locally hosted MaxMind-compatible City database. The
  request IP exists only long enough for the in-process lookup and is never stored or logged;
  analytics events retain only country/region names and codes. Private, loopback, link-local,
  carrier-grade NAT, and reserved addresses are ignored. Location is approximate.
- SentinelGPT-assisted, network-specific Facebook, X, and LinkedIn copy with editable
  previews, per-channel character limits, drafts, scheduling, delivery state, and retry.
- Encrypted social access tokens. Tokens remain on the server and are never returned to the
  browser after save.
- Direct text-post publishing through the Facebook Pages Graph API, X API v2, and LinkedIn
  Posts API. External developer/app approval and the network-specific user permissions are
  still prerequisites; GWS cannot bypass those platform controls.

## Parity roadmap

The three reference products cover years of features, so "parity" is tracked explicitly
rather than represented as one finished checkbox.

### Analytics phase 2

- CSV export, scheduled email reports, annotations, and comparison periods

### Analytics phase 3

- Entry/exit flow and user-journey reports
- Revenue/ecommerce events tied to first-party conversion ids
- Search Console and ad-platform attribution imports
- Bot and referrer-spam rules with an auditable exclusion log
- Data-retention controls and automatic aggregate rollups
- Optional self-hosted session replay and heatmaps only after a separate consent, redaction,
  storage, and security design; these are intentionally not hidden inside basic analytics

### Social phase 2

- OAuth connection flows and token refresh for all three networks
- Image/video upload and platform-native preview cards
- Per-network content calendars, approval roles, and reusable campaign templates
- Pull engagement metrics back into the analytics dashboard
- Comments/inbox triage and UTM campaign builder
- Automation nodes for publish, approval, and performance-triggered follow-up

## Channel permissions

- Facebook: a Page access token authorized for the target Page with `pages_manage_posts`
- X: an OAuth 2.0 user access token with `tweet.write`
- LinkedIn member: `w_member_social`
- LinkedIn organization: an approved application and `w_organization_social`

Platform access tiers and review rules can change. Validate them against the current official
developer documentation before connecting production accounts.

## Local GeoIP database setup

Geography is optional. GWS Business Suite starts normally without a database and shows a setup
state in Growth Studio. Existing enriched history remains reportable if the database is
temporarily unavailable.

1. Create a MaxMind account and download the GeoLite2 City MMDB from the official
   [GeoLite database page](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data).
   Accept and follow MaxMind's license and update requirements.
2. From the droplet, copy the downloaded file into the web container's persistent data volume:

   ```bash
   cd /opt/gwssuite
   docker compose cp /absolute/path/GeoLite2-City.mmdb \
     gwssuite:/app/data/GeoLite2-City.mmdb
   docker compose restart gwssuite
   ```

3. Open Growth Studio. The Audience geography panel should say **Local database ready**.
   Only visits received after setup gain location data; no historical IP data exists to
   backfill by design.

The default location is `/app/data/GeoLite2-City.mmdb`. To use another container path, set
`ANALYTICS_GEOIP_DATABASE_PATH` in the deployment `.env` file. Never commit an MMDB file to
the repository; `*.mmdb` is ignored.
